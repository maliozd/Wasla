using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// A minimal Chrome DevTools Protocol driver for layout checks that need a real rendering engine. It launches an
/// installed Chromium browser (Edge or Chrome, or the one named by <c>WASLA_TEST_BROWSER</c>) headless with a
/// throw-away profile, and is disposed with its whole process tree. No package dependency.
/// </summary>
internal sealed class HeadlessChromium : IAsyncDisposable
{
    public const string BrowserVariable = "WASLA_TEST_BROWSER";

    private readonly Process _process;
    private readonly string _profile;
    private readonly ClientWebSocket _socket;
    private int _nextId;

    private HeadlessChromium(Process process, string profile, ClientWebSocket socket)
    {
        _process = process;
        _profile = profile;
        _socket = socket;
    }

    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(BrowserVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        var relative = new[]
        {
            Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine("Google", "Chrome", "Application", "chrome.exe")
        };
        return roots
            .Where(root => !string.IsNullOrEmpty(root))
            .SelectMany(root => relative.Select(path => Path.Combine(root, path)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>How long the browser may take to write a usable DevTools port file and expose a page target.</summary>
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    public static Task<HeadlessChromium> StartAsync(string executable, CancellationToken ct) =>
        StartAsync(executable, StartupTimeout, Process.Start, ct);

    /// <summary>
    /// Starts the browser through <paramref name="launch"/> (tests substitute another process). When startup fails or
    /// is cancelled, the process tree is killed and the profile deleted before the exception propagates.
    /// </summary>
    internal static async Task<HeadlessChromium> StartAsync(
        string executable, TimeSpan timeout, Func<ProcessStartInfo, Process?> launch, CancellationToken ct)
    {
        var profile = Directory.CreateTempSubdirectory("wasla-headless-").FullName;
        Process? process = null;
        ClientWebSocket? socket = null;
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in new[]
                     {
                         "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                         "--disable-extensions", "--disable-background-networking", "--remote-debugging-port=0",
                         $"--user-data-dir={profile}", "about:blank"
                     })
                start.ArgumentList.Add(argument);

            var browser = launch(start) ?? throw new InvalidOperationException("The browser did not start.");
            process = browser;
            browser.BeginErrorReadLine();
            browser.BeginOutputReadLine();

            // Edge may still hold the file open while writing it, so a non-empty file is not yet a readable one.
            var startup = Stopwatch.StartNew();
            var endpoint = await DevToolsActivePort.WaitAsync(
                token => DevToolsActivePort.ReadAsync(Path.Combine(profile, DevToolsActivePort.FileName), token),
                () => DescribeExit(browser), () => startup.Elapsed, Task.Delay, timeout, ct);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{endpoint.Port}") };
            string? pageSocket = null;
            while (pageSocket is null)
            {
                var targets = await http.GetFromJsonAsync<JsonElement>("/json/list", ct);
                pageSocket = targets.EnumerateArray()
                    .Where(t => t.GetProperty("type").GetString() == "page")
                    .Select(t => t.GetProperty("webSocketDebuggerUrl").GetString())
                    .FirstOrDefault();
                if (pageSocket is null)
                {
                    if (startup.Elapsed > timeout)
                        throw new TimeoutException("The browser exposed no page target.");
                    await Task.Delay(100, ct);
                }
            }

            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(pageSocket), ct);
            return new HeadlessChromium(browser, profile, socket);
        }
        catch
        {
            socket?.Dispose();
            await CleanUpAsync(process, profile);
            throw;
        }
    }

    private static string? DescribeExit(Process process)
    {
        if (!process.HasExited)
            return null;
        try { return $"exited with code {process.ExitCode}"; }
        catch (InvalidOperationException) { return "exited"; }
    }

    public Task SetCookieAsync(Uri origin, string name, string value, CancellationToken ct) =>
        SendAsync("Network.setCookie", new { name, value, url = origin.GetLeftPart(UriPartial.Authority) + "/" }, ct);

    public Task SetViewportAsync(int width, int height, bool mobile, CancellationToken ct) =>
        SendAsync("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile }, ct);

    /// <summary>Emulates the operating-system color scheme ("light" or "dark"); open pages receive the change event.</summary>
    public Task SetSystemColorSchemeAsync(string scheme, CancellationToken ct) =>
        SendAsync("Emulation.setEmulatedMedia", new { features = new[] { new { name = "prefers-color-scheme", value = scheme } } }, ct);

    /// <summary>Runs a script in every new document before any of the page's own scripts.</summary>
    public async Task AddScriptBeforePageScriptsAsync(string source, CancellationToken ct)
    {
        await SendAsync("Page.enable", new { }, ct);
        await SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source }, ct);
    }

    /// <summary>Presses and releases one key as real keyboard input (Escape, Tab, Enter or a space), with optional Shift.</summary>
    public async Task PressKeyAsync(string key, bool shift, CancellationToken ct)
    {
        var (code, keyCode, text) = key switch
        {
            "Escape" => ("Escape", 27, (string?)null),
            "Tab" => ("Tab", 9, null),
            "Enter" => ("Enter", 13, "\r"),
            " " => ("Space", 32, " "),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unsupported key.")
        };
        var modifiers = shift ? 8 : 0;
        // CDP rejects a null text field, so keys without text (Escape, Tab) send a rawKeyDown without it.
        object down = text is null
            ? new { type = "rawKeyDown", key, code, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode, modifiers }
            : new { type = "keyDown", key, code, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode, modifiers, text };
        await SendAsync("Input.dispatchKeyEvent", down, ct);
        await SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, code, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode, modifiers }, ct);
    }

    /// <summary>A real left mouse click at a viewport point: whatever element is on top there receives it.</summary>
    public async Task ClickAtAsync(double x, double y, CancellationToken ct)
    {
        await SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, ct);
        await SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 }, ct);
        await SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 }, ct);
    }

    /// <summary>A real mouse-wheel scroll at a viewport point.</summary>
    public Task WheelAsync(double x, double y, double deltaY, CancellationToken ct) =>
        SendAsync("Input.dispatchMouseEvent", new { type = "mouseWheel", x, y, deltaX = 0, deltaY }, ct);

    /// <summary>Navigates and waits until the new document has finished loading.</summary>
    public async Task NavigateAsync(Uri url, CancellationToken ct)
    {
        await SendAsync("Page.navigate", new { url = url.ToString() }, ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await EvaluateAsync<bool>(
                   $"location.href === {JsonSerializer.Serialize(url.ToString())} && document.readyState === 'complete'", ct) is false)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{url} did not finish loading.");
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Polls a boolean expression until it is true or the time runs out.</summary>
    public async Task<bool> WaitForAsync(string expression, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await EvaluateAsync<bool>(expression, ct))
                return true;
            await Task.Delay(100, ct);
        }

        return false;
    }

    public async Task<T> EvaluateAsync<T>(string expression, CancellationToken ct)
    {
        var response = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, ct);
        var result = response.GetProperty("result");
        if (result.TryGetProperty("exceptionDetails", out var error))
            throw new InvalidOperationException("Script failed: " + error);
        return result.GetProperty("result").GetProperty("value").Deserialize<T>()!;
    }

    private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await _socket.ReceiveAsync(buffer, ct);
                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(message.ToArray()));
            // Events (no id) and other responses are skipped; only this command's reply is returned.
            if (document.RootElement.TryGetProperty("id", out var replyId) && replyId.GetInt32() == id)
            {
                if (document.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException($"{method} failed: {error}");
                return document.RootElement.Clone();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _socket.Dispose(); } catch (WebSocketException) { }
        await CleanUpAsync(_process, _profile);
    }

    /// <summary>Kills the browser's whole process tree, if it started, and deletes its throw-away profile.</summary>
    private static async Task CleanUpAsync(Process? process, string profile)
    {
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
            finally
            {
                process.Dispose();
            }
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(profile, recursive: true); break; }
            catch (IOException) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) { await Task.Delay(200); }
        }
    }
}
