using Wasla.PrintBridge.Configuration;

[assembly: AssemblyFixture(typeof(Wasla.PrintBridge.Tests.EscapedWriteCanary))]

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// For the whole test run, the Print Bridge default location (the machine's ProgramData, or a development root) is
/// replaced by an empty canary folder that nothing should ever create. Every test that writes does so in its own
/// <see cref="IsolatedDataRoot"/>; a write that escapes one, such as a shutdown save that runs after its test released
/// the root, lands here instead of in the real settings, token or history, and fails the run.
/// </summary>
public sealed class EscapedWriteCanary : IDisposable
{
    static EscapedWriteCanary()
    {
        Root = Path.Combine(Path.GetTempPath(), "wasla-pb-tests", "escaped-writes-" + Guid.NewGuid().ToString("N"));
        PrintBridgePaths.UseDefaultRootForTests(Root);
    }

    public EscapedWriteCanary() => EnsureInstalled();

    /// <summary>Never created by a test; anything here was written outside every isolated root.</summary>
    public static string Root { get; }

    /// <summary>Installs the canary (once per process) before a test redirects or writes anything.</summary>
    public static void EnsureInstalled()
    {
        // Reading Root runs the static constructor.
        _ = Root;
    }

    public static IReadOnlyList<string> Entries() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
                .Select(entry => Path.GetRelativePath(Root, entry))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    public static void AssertNothingEscaped(string when)
    {
        var entries = Entries();
        if (entries.Count > 0)
        {
            throw new InvalidOperationException(
                $"A Print Bridge write escaped its isolated test root {when}: {string.Join(", ", entries.Take(10))} in {Root}. "
                + "Without the canary it would have reached the machine's ProgramData.");
        }
    }

    public void Dispose() => AssertNothingEscaped("during the test run");
}

/// <summary>
/// A test class's private Print Bridge data root: every path points here from construction until <see cref="Dispose"/>.
/// Dispose first waits for every thread registered with <see cref="Track"/> (the tray application saves its settings
/// when it exits), then releases the redirect, then fails if anything reached the <see cref="EscapedWriteCanary"/>.
/// If a thread does not end in time, the redirect is kept for the rest of the run, so its late writes still land here.
/// </summary>
internal sealed class IsolatedDataRoot : IDisposable
{
    private static readonly TimeSpan DefaultThreadTimeout = TimeSpan.FromMinutes(2);

    private readonly IDisposable _scope;
    private readonly TimeSpan _threadTimeout;
    private readonly List<Thread> _threads = [];
    private bool _released;

    public IsolatedDataRoot(string purpose, TimeSpan? threadTimeout = null)
    {
        EscapedWriteCanary.EnsureInstalled();
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), purpose, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        _scope = PrintBridgePaths.UseRootForTests(Path);
        _threadTimeout = threadTimeout ?? DefaultThreadTimeout;
    }

    public string Path { get; }

    public string ConfigPath => System.IO.Path.Combine(Path, "appsettings.json");

    /// <summary>Set when teardown begins, before any thread is awaited.</summary>
    public ManualResetEventSlim TeardownStarted { get; } = new();

    /// <summary>A thread that may still write when the test body has finished, such as an application's message loop.</summary>
    public void Track(Thread thread)
    {
        lock (_threads)
            _threads.Add(thread);
    }

    /// <summary>Waits for every tracked thread. Throws, keeping the redirect active, when one does not end in time.</summary>
    public void WaitForThreads()
    {
        TeardownStarted.Set();
        Thread[] threads;
        lock (_threads)
            threads = [.. _threads];

        foreach (var thread in threads)
        {
            if (!thread.Join(_threadTimeout))
            {
                throw new InvalidOperationException(
                    $"A test thread did not end within {_threadTimeout}. The isolated data root stays active for the rest "
                    + "of the run, so a late write cannot reach the machine's settings.");
            }
        }
    }

    /// <summary>
    /// Waits for every tracked thread, then releases the redirect and fails if anything reached the canary. The folder
    /// is kept, so a test can still read what was written to it.
    /// </summary>
    public void Release()
    {
        if (_released)
            return;

        WaitForThreads();
        _released = true;
        _scope.Dispose();
        TeardownStarted.Dispose();
        EscapedWriteCanary.AssertNothingEscaped($"while the test using {Path} ran");
    }

    public void Dispose()
    {
        Release();
        for (var attempt = 0; attempt < 10 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A WebView2 browser process releases its profile shortly after its window closes.
                Thread.Sleep(300);
            }
        }
    }
}
