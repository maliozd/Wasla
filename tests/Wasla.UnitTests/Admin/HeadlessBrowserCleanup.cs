using System.ComponentModel;
using System.Globalization;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Stops the processes of one browser start and deletes its profile, within bounded time. A process-tree kill that
/// throws does not end the cleanup: every tracked process is then checked, and terminated on its own while it still
/// runs, until none runs any more or <see cref="ProcessExitTimeout"/> has passed. A process that has stopped but that
/// Windows is still tearing down (an already-exiting process) is not a failure by itself; one that still runs is. The
/// profile is deleted on every path, retrying while files are still held, until <see cref="ProfileReleaseTimeout"/>.
/// Anything left behind is reported with <see cref="HeadlessBrowserCleanupException"/>, naming the profile and the
/// state of each process.
/// The cleanup runs once; later and concurrent calls share its outcome. It takes no cancellation token: a cancelled
/// test must still release its browser, and the deadlines bound the wait.
/// </summary>
internal sealed class HeadlessBrowserCleanup
{
    /// <summary>How long a browser process may keep running after the kill.</summary>
    internal static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the profile may stay locked (by a process still exiting, or a scanner) before cleanup fails.</summary>
    internal static readonly TimeSpan ProfileReleaseTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The pause between checks of the tracked processes, and before the first retry of the profile deletion.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The least time between two process snapshots while a process still runs.</summary>
    internal static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(1);

    /// <summary>The longest pause between profile deletion attempts; the pause doubles from <see cref="PollInterval"/>.</summary>
    internal static readonly TimeSpan MaxDeleteInterval = TimeSpan.FromSeconds(1);

    private readonly IBrowserProcessTree? _processes;
    private readonly BrowserProfile _profile;
    private readonly Func<TimeSpan> _now;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Lazy<Task> _run;

    /// <param name="processes">The tree to stop, or null when no process was started. Disposed by the cleanup.</param>
    /// <param name="now">A monotonic clock; tests substitute one with <paramref name="delay"/>.</param>
    public HeadlessBrowserCleanup(
        IBrowserProcessTree? processes, BrowserProfile profile, Func<TimeSpan>? now = null, Func<TimeSpan, Task>? delay = null)
    {
        _processes = processes;
        _profile = profile;
        _now = now ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        _delay = delay ?? (pause => Task.Delay(pause));
        _run = new Lazy<Task>(RunOnceAsync);
    }

    public Task RunAsync() => _run.Value;

    private async Task RunOnceAsync()
    {
        var problems = new List<string>();
        var details = new List<string>();
        try
        {
            if (_processes is not null)
                await StopProcessesAsync(_processes, problems, details);
        }
        finally
        {
            _processes?.Dispose();
        }

        await DeleteProfileAsync(problems, details);

        if (problems.Count > 0)
            throw new HeadlessBrowserCleanupException(
                $"Headless browser cleanup failed for profile {_profile.Path}: {string.Join("; ", problems)}."
                + Environment.NewLine + string.Join(Environment.NewLine, details));
    }

    private async Task StopProcessesAsync(IBrowserProcessTree tree, List<string> problems, List<string> details)
    {
        var started = _now();
        IReadOnlyList<IBrowserProcess> tracked;
        HashSet<int> exitedBefore;
        try
        {
            // Every process the browser created so far, found before the kill while their parents still run.
            tracked = tree.Refresh();
            exitedBefore = [.. tracked.Where(p => p.HasExited).Select(p => p.Id)];
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            tracked = [];
            exitedBefore = [];
            details.Add($"The browser's processes could not be listed before the kill: {Describe(ex)}");
        }

        Exception? killError = null;
        try
        {
            tree.KillTree();
        }
        catch (Exception ex)
        {
            // For example AggregateException with Win32Exception "Access is denied" for a child that is already exiting.
            killError = ex;
            details.Add($"The process-tree kill threw {Describe(ex)}");
        }

        var terminationErrors = new Dictionary<int, Exception>();
        var failedOnce = new HashSet<int>();
        List<IBrowserProcess> exiting;
        List<IBrowserProcess> running;
        // A process snapshot lists every process on the machine and competes with process creation and exit, so the
        // tree is listed again only right after the kill (a child created before it took effect) and then at most once a
        // second while a process still runs. Between snapshots only the tracked handles are checked.
        var lastSnapshot = TimeSpan.MinValue;
        while (true)
        {
            if (lastSnapshot == TimeSpan.MinValue || _now() - lastSnapshot >= SnapshotInterval)
            {
                lastSnapshot = _now();
                try
                {
                    tracked = tree.Refresh();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    details.Add($"The browser's processes could not be listed again: {Describe(ex)}");
                }
            }

            // A stopped process runs no more code, but it releases its files only once Windows has torn it down, so
            // the profile is not deleted before then; at the deadline a stopped process is not a failure by itself.
            exiting = [.. tracked.Where(p => !p.HasExited)];
            running = [.. exiting.Where(p => !p.HasStopped)];
            var remaining = ProcessExitTimeout - (_now() - started);
            if (exiting.Count == 0 || remaining <= TimeSpan.Zero)
                break;
            if (running.Count == 0)
            {
                await _delay(remaining < PollInterval ? remaining : PollInterval);
                continue;
            }
            // Whatever the tree kill missed or could not stop yet (a child created after it, one that refused).
            foreach (var process in running)
            {
                try
                {
                    process.Terminate();
                    terminationErrors.Remove(process.Id);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    terminationErrors[process.Id] = ex;
                    failedOnce.Add(process.Id);
                }
            }

            await _delay(remaining < PollInterval ? remaining : PollInterval);
        }

        foreach (var process in tracked)
        {
            var refused = failedOnce.Contains(process.Id) ? "; terminating it failed while it was already exiting" : "";
            var state = exitedBefore.Contains(process.Id) ? "had exited before cleanup"
                : running.Contains(process) && terminationErrors.TryGetValue(process.Id, out var error) ? $"still running; terminating it failed: {Describe(error)}"
                : running.Contains(process) ? "still running"
                : exiting.Contains(process) ? "stopped, but Windows had not finished tearing it down" + refused
                : "exited after the kill" + refused;
            details.Add($"Process {process.Name} {process.Id.ToString(CultureInfo.InvariantCulture)}: {state}.");
        }

        if (running.Count > 0)
            problems.Add($"{running.Count} of {tracked.Count} browser processes still running {Seconds(_now() - started)} after the kill"
                + (killError is null ? "" : $" (the process-tree kill threw {killError.GetType().Name})"));
    }

    private async Task DeleteProfileAsync(List<string> problems, List<string> details)
    {
        var started = _now();
        var pause = PollInterval;
        Exception? lastError = null;
        while (true)
        {
            // Checked before every attempt: only the directory the browser start created is ever deleted.
            if (_profile.WhyNotDeletable() is { } refusal)
            {
                problems.Add($"the profile was not deleted because {refusal}");
                return;
            }

            try
            {
                Directory.Delete(_profile.Path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
            }

            // Each attempt walks the whole profile again, so the pause grows while files stay held.
            var remaining = ProfileReleaseTimeout - (_now() - started);
            if (remaining <= TimeSpan.Zero)
                break;
            await _delay(remaining < pause ? remaining : pause);
            pause = pause * 2 < MaxDeleteInterval ? pause * 2 : MaxDeleteInterval;
        }

        problems.Add($"the profile could not be deleted within {Seconds(ProfileReleaseTimeout)}");
        details.Add($"Last delete error: {Describe(lastError!)}");
        details.Add($"Profile entries left: {DescribeRemaining(_profile.Path)}");
    }

    private static string DescribeRemaining(string path)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Select(entry => Path.GetRelativePath(path, entry))
                .Take(101)
                .ToList();
            var shown = string.Join(", ", entries.Take(5));
            return entries.Count switch
            {
                0 => "none (the empty directory itself)",
                > 100 => $"more than 100, including {shown}",
                <= 5 => $"{entries.Count}: {shown}",
                _ => $"{entries.Count}, including {shown}"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"unknown ({ex.GetType().Name})";
        }
    }

    private static string Describe(Exception error)
    {
        var inner = error is AggregateException aggregate
            ? " [" + string.Join("; ", aggregate.Flatten().InnerExceptions.Select(Describe)) + "]"
            : "";
        var code = error is Win32Exception win32 ? $" (Win32 error {win32.NativeErrorCode.ToString(CultureInfo.InvariantCulture)})" : "";
        return $"{error.GetType().Name}{code}: {error.Message}{inner}";
    }

    private static string Seconds(TimeSpan time) =>
        time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}

/// <summary>A headless browser's processes or profile outlived its cleanup. The message names what was left.</summary>
internal sealed class HeadlessBrowserCleanupException(string message) : Exception(message);
