using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// A minimal Chrome DevTools Protocol driver for layout checks that need a real rendering engine. It launches an
/// installed Chromium browser (Edge or Chrome, or the one named by <c>WASLA_TEST_BROWSER</c>) headless with a
/// throw-away profile, and is disposed with its whole process tree (see <see cref="HeadlessBrowserCleanup"/>). A caller
/// owns the browser once it is in an <c>await using</c>; setup before that point goes through <see cref="SetUpAsync"/>.
/// No package dependency.
/// </summary>
internal sealed class HeadlessChromium : IAsyncDisposable
{
    public const string BrowserVariable = "WASLA_TEST_BROWSER";

    private readonly IBrowserProcessTree _processes;
    private readonly BrowserProfile _profile;
    private readonly HeadlessBrowserCleanup _cleanup;
    private readonly ClientWebSocket _socket;
    private int _nextId;

    // Media features the machine would otherwise decide. A GitHub runner reports reduced motion and a developer machine
    // usually does not, which changes the page (AdminLTE shows every .fade element under reduced motion), so every
    // browser starts with the same values.
    private readonly Dictionary<string, string> _mediaFeatures = new() { ["prefers-reduced-motion"] = "no-preference" };

    /// <summary>Takes ownership of the processes and the profile; tests construct it around a stand-in process.</summary>
    internal HeadlessChromium(IBrowserProcessTree processes, BrowserProfile profile, ClientWebSocket socket)
    {
        _processes = processes;
        _profile = profile;
        _socket = socket;
        _cleanup = new HeadlessBrowserCleanup(processes, profile);
    }

    /// <summary>The profile directory this browser was started with.</summary>
    internal string ProfilePath => _profile.Path;

    /// <summary>The processes this browser has started so far, the browser process first. Valid until disposal.</summary>
    internal IReadOnlyList<IBrowserProcess> TrackedProcesses() => _processes.Refresh();

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

    /// <summary>
    /// The Windows application-compatibility layer. Edge started with it relaunches itself without it (adding
    /// <c>--edge-skip-compat-layer-relaunch</c>) and the started process exits with code 0, so the browser is never
    /// the process the driver started. Some shells set it for every process they run.
    /// </summary>
    internal const string CompatibilityLayerVariable = "__COMPAT_LAYER";

    /// <summary>How long the browser may take to write a usable DevTools port file and expose a page target.</summary>
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    public static Task<HeadlessChromium> StartAsync(string executable, CancellationToken ct) =>
        StartAsync(executable, StartupTimeout, Process.Start, ct);

    /// <summary>Starts the browser with extra command-line switches, for example <c>--host-resolver-rules</c>.</summary>
    public static Task<HeadlessChromium> StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct) =>
        StartAsync(executable, StartupTimeout, Process.Start, ct, arguments);

    /// <summary>
    /// Starts the browser through <paramref name="launch"/> (tests substitute another process). When startup fails or
    /// is cancelled, the process tree is killed and the profile deleted before the exception propagates.
    /// </summary>
    internal static async Task<HeadlessChromium> StartAsync(
        string executable, TimeSpan timeout, Func<ProcessStartInfo, Process?> launch, CancellationToken ct,
        IReadOnlyList<string>? arguments = null)
    {
        var profile = BrowserProfile.Create();
        BrowserProcessTree? processes = null;
        ClientWebSocket? socket = null;
        HeadlessChromium started;
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
                         $"--user-data-dir={profile.Path}"
                     }.Concat(arguments ?? []).Append("about:blank"))
                start.ArgumentList.Add(argument);
            // The relaunched copy would be outside the started process tree: startup would fail when the started
            // process exits, and the copy would outlive cleanup. Without the layer, the started process is the browser.
            start.Environment.Remove(CompatibilityLayerVariable);

            var browser = launch(start) ?? throw new InvalidOperationException("The browser did not start.");
            processes = BrowserProcessTree.Track(browser, Path.GetFileName(executable));
            browser.BeginErrorReadLine();
            browser.BeginOutputReadLine();

            // Edge may still hold the file open while writing it, so a non-empty file is not yet a readable one.
            var startup = Stopwatch.StartNew();
            var endpoint = await DevToolsActivePort.WaitAsync(
                token => DevToolsActivePort.ReadAsync(Path.Combine(profile.Path, DevToolsActivePort.FileName), token),
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
            // Track the processes the browser started with while their parents still run; cleanup looks again.
            processes.Refresh();
            started = new HeadlessChromium(processes, profile, socket);
        }
        catch (Exception startupFailure)
        {
            socket?.Dispose();
            await CleanUpAfterAsync(startupFailure, new HeadlessBrowserCleanup(processes, profile).RunAsync);
            throw;
        }

        return await started.SetUpAsync(browser => browser.EmulateMediaAsync(ct));
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

    /// <summary>
    /// Slows the page's main thread by <paramref name="rate"/> (1 = no slowdown), as a loaded machine would, so a page is
    /// painted while it is still being parsed. Applies to this browser's page until it is closed.
    /// </summary>
    public Task SetCpuThrottlingAsync(double rate, CancellationToken ct) =>
        SendAsync("Emulation.setCPUThrottlingRate", new { rate }, ct);

    public Task SetViewportAsync(int width, int height, bool mobile, CancellationToken ct) =>
        SendAsync("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile }, ct);

    /// <summary>Emulates the operating-system color scheme ("light" or "dark"); open pages receive the change event.</summary>
    public Task SetSystemColorSchemeAsync(string scheme, CancellationToken ct)
    {
        _mediaFeatures["prefers-color-scheme"] = scheme;
        return EmulateMediaAsync(ct);
    }

    /// <summary>
    /// Emulates the operating-system motion preference ("reduce" or "no-preference") for this browser only; every browser
    /// starts with "no-preference".
    /// </summary>
    public Task SetReducedMotionAsync(string preference, CancellationToken ct)
    {
        _mediaFeatures["prefers-reduced-motion"] = preference;
        return EmulateMediaAsync(ct);
    }

    // Each call replaces the whole emulated list, so every feature set so far is sent again.
    private Task EmulateMediaAsync(CancellationToken ct) =>
        SendAsync("Emulation.setEmulatedMedia", new { features = _mediaFeatures.Select(f => new { name = f.Key, value = f.Value }).ToArray() }, ct);

    /// <summary>Runs a script in every new document before any of the page's own scripts.</summary>
    public async Task AddScriptBeforePageScriptsAsync(string source, CancellationToken ct)
    {
        await SendAsync("Page.enable", new { }, ct);
        await SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source }, ct);
    }

    /// <summary>
    /// Presses and releases one key as real keyboard input (Escape, Tab, Enter, a space, the left and right arrows, Home
    /// or End), with optional Shift.
    /// </summary>
    public async Task PressKeyAsync(string key, bool shift, CancellationToken ct)
    {
        var (code, keyCode, text) = key switch
        {
            "Escape" => ("Escape", 27, (string?)null),
            "Tab" => ("Tab", 9, null),
            "ArrowLeft" => ("ArrowLeft", 37, null),
            "ArrowRight" => ("ArrowRight", 39, null),
            "Home" => ("Home", 36, null),
            "End" => ("End", 35, null),
            "Enter" => ("Enter", 13, "\r"),
            " " => ("Space", 32, " "),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unsupported key.")
        };
        var modifiers = shift ? 8 : 0;
        // CDP rejects a null text field, so keys without text (Escape, Tab, arrows, Home, End) send a rawKeyDown without it.
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

    /// <summary>How long a navigation may take until the new document has finished loading.</summary>
    internal static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Navigates and waits until the new document has finished loading.</summary>
    public Task NavigateAsync(Uri url, CancellationToken ct) => NavigateAsync(url, NavigationTimeout, ct);

    /// <summary>
    /// Navigates and waits until the new document has finished loading. On timeout, the exception says where the page
    /// is and what it still waits for, so a failure on another machine (CI) can be diagnosed from the message alone.
    /// </summary>
    internal async Task NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        await SendAsync("Page.navigate", new { url = url.ToString() }, ct);
        var deadline = DateTime.UtcNow + timeout;
        while (await EvaluateAsync<bool>(
                   $"location.href === {JsonSerializer.Serialize(url.ToString())} && document.readyState === 'complete'", ct) is false)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{url} did not finish loading: {await DescribeLoadAsync(ct)}");
            await Task.Delay(100, ct);
        }
    }

    // What holds the load event back: the parser-blocking script (while the document is still loading), and images,
    // stylesheets, frames and media that have not finished. Same-origin URLs are shown as paths; a browser error page
    // (chrome-error://chromewebdata/, shown for example for an HTTP error without a body) keeps its scheme.
    private const string LoadState = """
        (() => {
          const short = u => {
            try {
              const x = new URL(u, location.href);
              if (!x.protocol.startsWith('http')) return (x.protocol + '//' + x.host + x.pathname).slice(0, 80);
              return (x.origin === location.origin ? '' : x.host) + x.pathname;
            } catch (e) { return String(u).slice(0, 80); }
          };
          const scripts = [...document.scripts], last = scripts[scripts.length - 1];
          const pending = [
            ...(document.readyState === 'loading' && last ? ['parser at ' + (last.src ? 'script ' + short(last.src) : 'inline script ' + scripts.length)] : []),
            ...[...document.images].filter(i => i.src && !i.complete).map(i => 'img ' + short(i.currentSrc || i.src)),
            ...[...document.querySelectorAll('link[rel~="stylesheet"][href]')].filter(l => !l.sheet).map(l => 'stylesheet ' + short(l.href)),
            ...[...document.querySelectorAll('iframe')].map(f => 'iframe ' + short(f.src || 'about:blank')),
            ...[...document.querySelectorAll('audio, video')].filter(m => m.readyState < 2 && m.networkState === 2).map(m => m.tagName.toLowerCase() + ' ' + short(m.currentSrc))
          ];
          return 'at ' + short(location.href) + location.search + ', readyState ' + document.readyState + ', fonts ' + document.fonts.status + ', '
            + (pending.length ? 'pending: ' + pending.slice(0, 10).join(', ') : 'no pending script, image, stylesheet, frame or media');
        })()
        """;

    private async Task<string> DescribeLoadAsync(CancellationToken ct)
    {
        try { return await EvaluateAsync<string>(LoadState, ct); }
        catch (InvalidOperationException ex) { return "page state unavailable (" + ex.Message + ")"; }
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

    /// <summary>
    /// The role and accessible name the browser computes for every element matching <paramref name="selector"/>, in
    /// document order, read from its accessibility tree (the CDP Accessibility domain) rather than recomputed in script.
    /// An element the tree ignores (for example one with <c>display: none</c>) has <see cref="AccessibleNode.Ignored"/>.
    /// </summary>
    public async Task<IReadOnlyList<AccessibleNode>> AccessibleNodesAsync(string selector, CancellationToken ct)
    {
        var document = await SendAsync("DOM.getDocument", new { depth = 0 }, ct);
        var root = document.GetProperty("result").GetProperty("root").GetProperty("nodeId").GetInt32();
        var matches = await SendAsync("DOM.querySelectorAll", new { nodeId = root, selector }, ct);
        var nodes = new List<AccessibleNode>();
        foreach (var nodeId in matches.GetProperty("result").GetProperty("nodeIds").EnumerateArray())
        {
            var tree = await SendAsync("Accessibility.getPartialAXTree", new { nodeId = nodeId.GetInt32(), fetchRelatives = false }, ct);
            var node = tree.GetProperty("result").GetProperty("nodes")[0];
            nodes.Add(new AccessibleNode(
                AxValue(node, "role"),
                AxValue(node, "name") ?? "",
                node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean()));
        }

        return nodes;
    }

    private static string? AxValue(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.TryGetProperty("value", out var text) ? text.GetString() : null;

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

    /// <summary>
    /// Runs setup on a browser its caller does not own yet: cookies, viewport, a first page. If setup throws (a failed
    /// command, a skip, cancellation), the browser is disposed before the exception propagates, because the caller's
    /// <c>await using</c> takes over only once this returns.
    /// </summary>
    public async Task<HeadlessChromium> SetUpAsync(Func<HeadlessChromium, Task> setUp)
    {
        try
        {
            await setUp(this);
            return this;
        }
        catch (Exception setUpFailure)
        {
            await CleanUpAfterAsync(setUpFailure, async () => await DisposeAsync());
            throw;
        }
    }

    /// <summary>
    /// Kills the browser's process tree and deletes its profile; repeated and concurrent calls share one cleanup. Throws
    /// <see cref="HeadlessBrowserCleanupException"/> when a process or the profile outlives the bounded cleanup.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try { _socket.Dispose(); } catch (WebSocketException) { }
        await _cleanup.RunAsync();
    }

    /// <summary>Cleans up after <paramref name="failure"/>; when the cleanup fails too, both are reported together.</summary>
    private static async Task CleanUpAfterAsync(Exception failure, Func<Task> cleanUp)
    {
        try
        {
            await cleanUp();
        }
        catch (HeadlessBrowserCleanupException cleanupFailure)
        {
            throw new AggregateException("The browser failed and its cleanup failed too.", failure, cleanupFailure);
        }
    }
}

/// <summary>One element as the browser's accessibility tree exposes it: its computed role and accessible name.</summary>
internal sealed record AccessibleNode(string? Role, string Name, bool Ignored);
