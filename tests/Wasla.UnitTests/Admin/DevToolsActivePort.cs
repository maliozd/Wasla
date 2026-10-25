using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The endpoint a Chromium browser started with <c>--remote-debugging-port=0</c> writes to the
/// <c>DevToolsActivePort</c> file in its profile: the port on the first line and the browser WebSocket path
/// (<c>/devtools/browser/{guid}</c>) on the second, with no trailing newline.
/// </summary>
internal sealed record DevToolsActivePort(int Port, string BrowserPath)
{
    public const string FileName = "DevToolsActivePort";

    /// <summary>The pause between reads while the file is absent, locked, empty or partly written.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);

    private const string BrowserPathPrefix = "/devtools/browser/";

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION: on Windows the browser still holds the file it is writing.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// Reads the file until it holds a complete endpoint. An absent, locked, empty or partly written file is read again
    /// after <see cref="RetryInterval"/>; any other read failure propagates unchanged. The wait ends when the browser
    /// exits (<paramref name="describeExit"/> returns its exit state) or when <paramref name="elapsed"/>, the time the
    /// whole startup has taken so far, reaches <paramref name="timeout"/>.
    /// </summary>
    public static async Task<DevToolsActivePort> WaitAsync(
        Func<CancellationToken, Task<string>> read,
        Func<string?> describeExit,
        Func<TimeSpan> elapsed,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan timeout,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string state;
            try
            {
                if (TryParse(await read(ct), out var endpoint, out var problem))
                    return endpoint;
                state = problem;
            }
            catch (FileNotFoundException)
            {
                state = "absent";
            }
            catch (IOException ex) when (ex.HResult is SharingViolation or LockViolation)
            {
                state = "locked by another process";
            }

            if (describeExit() is { } exit)
                throw new InvalidOperationException(
                    $"The browser {exit} after {Seconds(elapsed())} before its DevTools port was usable; {FileName} was {state}.");
            var remaining = timeout - elapsed();
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException(
                    $"The browser did not open a DevTools port within {Seconds(timeout)}; after {Seconds(elapsed())} {FileName} was {state}.");
            await delay(remaining < RetryInterval ? remaining : RetryInterval, ct);
        }
    }

    /// <summary>
    /// Parses a complete file. The browser path must end in a whole GUID, so a partly written file never parses, even
    /// when its first line already looks like a port.
    /// </summary>
    public static bool TryParse(string? content, [NotNullWhen(true)] out DevToolsActivePort? endpoint, out string problem)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            problem = "empty";
            return false;
        }

        var lines = content.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (!int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            problem = "malformed (no valid port on its first line)";
            return false;
        }

        if (lines.Length < 2
            || !lines[1].StartsWith(BrowserPathPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(lines[1][BrowserPathPrefix.Length..], "D", out _))
        {
            problem = "malformed (no complete browser WebSocket path on its second line)";
            return false;
        }

        endpoint = new DevToolsActivePort(port, lines[1]);
        problem = "";
        return true;
    }

    /// <summary>
    /// Reads the file with the widest sharing, so only a writer that holds it exclusively can refuse the read; a
    /// partly written result is caught by <see cref="TryParse"/>.
    /// </summary>
    public static async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    private static string Seconds(TimeSpan time) =>
        time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
