using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wasla.UnitTests.Admin;

/// <summary>One process of a browser start, as cleanup sees it.</summary>
internal interface IBrowserProcess
{
    int Id { get; }

    string Name { get; }

    /// <summary>The process is gone: Windows has finished tearing it down and released its handles and files.</summary>
    bool HasExited { get; }

    /// <summary>
    /// The process runs no more code: it has an exit code, although Windows may still be tearing it down (under load
    /// this took longer than ten seconds). True whenever <see cref="HasExited"/> is.
    /// </summary>
    bool HasStopped { get; }

    /// <summary>Terminates this process only. A process that has already stopped is not an error.</summary>
    void Terminate();
}

/// <summary>
/// The processes one browser start owns: the process the driver started and its descendants. A process is never
/// chosen by its name or command line, so another browser, Edge window or WebView2 host is never part of it.
/// </summary>
internal interface IBrowserProcessTree : IDisposable
{
    /// <summary>Starts tracking descendants created since the last call and returns every tracked process, the started one first.</summary>
    IReadOnlyList<IBrowserProcess> Refresh();

    /// <summary>Kills the started process and its descendants in one call. Throws when a process refuses, for example while it is already exiting.</summary>
    void KillTree();
}

/// <summary>One row of a process snapshot.</summary>
internal readonly record struct ProcessEntry(int Id, int ParentId, string Name);

/// <summary>
/// Tracks the processes a browser start created, from the started process down. A descendant is a process whose parent
/// is tracked and which was created no earlier than that parent. An open handle is held to every tracked process, so its
/// id cannot pass to an unrelated process while it is tracked. A candidate is confirmed only after its handle is open: a
/// process id from a snapshot may already have been reused when it is opened, so a second snapshot, taken while the
/// handle pins the opened process, must still show the tracked parent. On other platforms only the started process is
/// tracked and <see cref="KillTree"/> reaches the rest.
/// </summary>
internal sealed class BrowserProcessTree : IBrowserProcessTree
{
    private readonly Action _killTree;
    private readonly Func<IReadOnlyList<ProcessEntry>> _snapshot;
    private readonly Func<int, string, TrackedProcess?> _open;
    private readonly List<TrackedProcess> _tracked = [];

    /// <summary>Tests substitute the snapshot and the way a process is opened.</summary>
    internal BrowserProcessTree(
        TrackedProcess started, Action killTree, Func<IReadOnlyList<ProcessEntry>> snapshot, Func<int, string, TrackedProcess?> open)
    {
        _killTree = killTree;
        _snapshot = snapshot;
        _open = open;
        _tracked.Add(started);
    }

    /// <summary>Tracks <paramref name="started"/>, which the caller no longer disposes, and the processes it creates.</summary>
    public static BrowserProcessTree Track(Process started, string name)
    {
        // Elsewhere only the started process is tracked, so the runtime's own tree kill has to find the rest.
        if (!OperatingSystem.IsWindows())
            return new(new StartedProcess(started, name), () => started.Kill(entireProcessTree: true), () => [], (_, _) => null);

        BrowserProcessTree? tree = null;
        tree = new(new StartedProcess(started, name), () => tree!.TerminateTracked(), Native.Snapshot, DescendantProcess.TryOpen);
        return tree;
    }

    public IReadOnlyList<IBrowserProcess> Refresh()
    {
        var snapshot = _snapshot();
        var rejected = new HashSet<int>();
        while (true)
        {
            var candidates = new List<(TrackedProcess Child, int ParentId, long ParentCreated)>();
            var added = false;
            try
            {
                foreach (var (id, parentId, name) in snapshot)
                {
                    if (rejected.Contains(id) || _tracked.Any(p => p.Id == id))
                        continue;
                    if (_tracked.FirstOrDefault(p => p.Id == parentId)?.CreatedAt is not { } parentCreated)
                        continue;
                    if (_open(id, name) is { } child)
                        candidates.Add((child, parentId, parentCreated));
                }

                if (candidates.Count == 0)
                    return [.. _tracked];

                // The handles now pin the opened processes, so this snapshot shows their real parents. It also lists any
                // children of the processes added below, for the next pass.
                snapshot = _snapshot();
                var parents = snapshot.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().ParentId);
                foreach (var (child, parentId, parentCreated) in candidates)
                {
                    // Not ours: the id passed to another process before it was opened, or a process older than its supposed
                    // parent got that parent id from an earlier, unrelated process. Edge creates its first children within
                    // the same clock tick as itself, so an equal creation time is its own.
                    if (!parents.TryGetValue(child.Id, out var actualParent) || actualParent != parentId
                        || child.CreatedAt is not { } created || created < parentCreated)
                    {
                        rejected.Add(child.Id);
                        continue;
                    }

                    _tracked.Add(child);
                    added = true;
                }
            }
            finally
            {
                // Only a handle the tree now tracks stays open: a rejected candidate's, and every candidate's when the
                // verification snapshot throws, is released here.
                foreach (var (child, _, _) in candidates)
                {
                    if (!_tracked.Contains(child))
                        child.Dispose();
                }
            }

            if (!added)
                return [.. _tracked];
        }
    }

    public void KillTree() => _killTree();

    /// <summary>
    /// Terminates every tracked process through its own handle, the started one first so it creates no more children.
    /// Process.Kill(entireProcessTree) is not used: it opens and queries every process on the machine once per
    /// descendant, synchronously, which under load took longer than the whole cleanup may (about 20 s), and it would
    /// look for descendants a second time when this tree already knows them.
    /// </summary>
    internal void TerminateTracked()
    {
        List<Exception>? failures = null;
        foreach (var process in _tracked)
        {
            try
            {
                if (!process.HasStopped)
                    process.Terminate();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException("Not every browser process could be terminated.", failures);
    }

    public void Dispose()
    {
        foreach (var process in _tracked)
            process.Dispose();
        _tracked.Clear();
    }

    /// <summary>
    /// A handle that only observes whether a process has exited, opened while the process is tracked so its id still
    /// names it. Tests use it to check processes after the tree has been disposed.
    /// </summary>
    public static ProcessWatch Watch(IBrowserProcess process)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process watches use Windows process handles.");
        var handle = Native.OpenProcess(Native.Synchronize | Native.QueryLimitedInformation, false, process.Id);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Process {process.Id} ({process.Name}) could not be opened.");
        return new ProcessWatch(process.Id, process.Name, handle);
    }

    /// <summary>A process with the creation time that ties it to its parent, and the handle that keeps its id its own.</summary>
    internal abstract class TrackedProcess : IBrowserProcess, IDisposable
    {
        public abstract int Id { get; }

        public abstract string Name { get; }

        /// <summary>Creation time as a Windows file time; null when it could not be read.</summary>
        public abstract long? CreatedAt { get; }

        public abstract bool HasExited { get; }

        public abstract bool HasStopped { get; }

        public abstract void Terminate();

        public abstract void Dispose();
    }

    private sealed class StartedProcess(Process process, string name) : TrackedProcess
    {
        // Without it no descendant can be verified, and cleanup relies on KillTree alone; tracking itself must not fail.
        private readonly long? _createdAt = ReadCreationTime(process);

        public override int Id { get; } = process.Id;

        public override string Name => name;

        public override long? CreatedAt => _createdAt;

        // Process.HasExited is true once an exit code is set, while a terminated process may still be tearing down and
        // holding files; only a signaled handle means it is gone.
        public override bool HasExited
        {
            get
            {
                try { return OperatingSystem.IsWindows() ? Native.HasExited(process.SafeHandle) : process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public override bool HasStopped
        {
            get
            {
                try { return OperatingSystem.IsWindows() ? Native.HasStopped(process.SafeHandle) : process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        // Process.Kill returns quietly once the process has exited and throws while it is still exiting.
        public override void Terminate() => process.Kill();

        public override void Dispose() => process.Dispose();

        private static long? ReadCreationTime(Process process)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            try { return Native.CreationTime(process.SafeHandle); }
            catch (InvalidOperationException) { return null; }
        }
    }

    private sealed class DescendantProcess : TrackedProcess
    {
        private readonly SafeProcessHandle _handle;

        private DescendantProcess(int id, string name, SafeProcessHandle handle)
        {
            Id = id;
            Name = name;
            _handle = handle;
            CreatedAt = Native.CreationTime(handle);
        }

        public override int Id { get; }

        public override string Name { get; }

        public override long? CreatedAt { get; }

        public override bool HasExited => Native.HasExited(_handle);

        public override bool HasStopped => Native.HasStopped(_handle);

        public override void Terminate() => Native.Terminate(_handle, Id);

        public override void Dispose() => _handle.Dispose();

        /// <summary>Null when the process has already gone or cannot be opened, in which case it cannot be verified as ours.</summary>
        public static DescendantProcess? TryOpen(int id, string name)
        {
            var handle = Native.OpenProcess(Native.Synchronize | Native.QueryLimitedInformation | Native.TerminateAccess, false, id);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                // Without terminate access the process can still be watched, and Terminate reports the refusal.
                handle = Native.OpenProcess(Native.Synchronize | Native.QueryLimitedInformation, false, id);
            }

            if (!handle.IsInvalid)
                return new DescendantProcess(id, name, handle);
            handle.Dispose();
            return null;
        }
    }

    internal sealed class ProcessWatch(int id, string name, SafeProcessHandle handle) : IDisposable
    {
        public int Id => id;

        public string Name => name;

        public bool HasExited => Native.HasExited(handle);

        /// <summary>Runs no more code, although Windows may still be tearing it down.</summary>
        public bool HasStopped => Native.HasStopped(handle);

        public void Dispose() => handle.Dispose();

        public override string ToString() => $"{name} {id}";
    }

    private static class Native
    {
        public const uint TerminateAccess = 0x0001;
        public const uint QueryLimitedInformation = 0x1000;
        public const uint Synchronize = 0x00100000;

        private const uint SnapProcess = 0x00000002;
        private const uint WaitObject0 = 0;
        private const int StillActive = 259;

        public static IReadOnlyList<ProcessEntry> Snapshot()
        {
            using var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snapshot.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The process snapshot could not be taken.");
            var entries = new List<ProcessEntry>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                entries.Add(new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            return entries;
        }

        public static long? CreationTime(SafeProcessHandle handle) =>
            GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null;

        public static bool HasExited(SafeProcessHandle handle) => WaitForSingleObject(handle, 0) == WaitObject0;

        // An exit code other than STILL_ACTIVE is set when termination starts, before the process object is signaled.
        public static bool HasStopped(SafeProcessHandle handle) =>
            HasExited(handle) || (GetExitCodeProcess(handle, out var exitCode) && exitCode != StillActive);

        public static void Terminate(SafeProcessHandle handle, int id)
        {
            if (TerminateProcess(handle, 1))
                return;
            var error = Marshal.GetLastWin32Error();
            // Access is denied once the process has exited; that is the result cleanup wants.
            if (GetExitCodeProcess(handle, out var exitCode) && exitCode != StillActive)
                return;
            throw new Win32Exception(error, $"Process {id} could not be terminated.");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    }
}
