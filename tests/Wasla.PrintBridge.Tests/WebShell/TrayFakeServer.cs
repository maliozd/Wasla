using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// A local stand-in for the Wasla device API, for the real tray application (which builds its own HTTP client) to listen
/// to. It accepts only <see cref="Token"/> (a fake value), follows the job contract of <c>PrintBridgeJobService</c>
/// (conditional transitions, a repeat answered <c>skipped</c>) and records every request by path. The answer to the
/// claim can be held with <see cref="HoldClaimAnswer"/>, so a job is deterministically active while the test acts.
/// </summary>
internal sealed class TrayFakeServer : IDisposable
{
    public const string Token = "tray-test-token-not-a-real-credential";

    private readonly HttpListener _listener = new();
    private readonly object _sync = new();
    private readonly Dictionary<Guid, string> _jobs = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _finished = [];
    private readonly Task _serving;
    private TaskCompletionSource _nextPoll = NewSignal();

    public TrayFakeServer()
    {
        var port = FreePort();
        Url = $"http://localhost:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public string Url { get; }

    /// <summary>Every request that reached the server: method and path, without the query.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Every request with the Print Bridge version that sent it (its <c>X-PrintBridge-Version</c> header, a.b.c.d).</summary>
    public ConcurrentQueue<(string Version, string Request)> VersionedRequests { get; } = new();

    /// <summary>When set, the claim (mark-printing) is processed and then its answer is held until the gate opens.</summary>
    public TrayGate? HoldClaimAnswer { get; set; }

    /// <summary>When set, every printed report is held and then refused without being processed (WAS-61 recovery tests).</summary>
    public TrayGate? DropPrintedReports { get; set; }

    /// <summary>The release this server offers (WAS-63); null answers 204 No Content, as the Wasla server without a release.</summary>
    public TrayFakeRelease? Release { get; set; }

    /// <summary>When set, a setup download sends half of the file, then waits until the gate opens.</summary>
    public TrayGate? HoldSetupDownload { get; set; }

    public Guid AddJob()
    {
        var id = Guid.NewGuid();
        lock (_sync)
        {
            _jobs[id] = "Pending";
            _finished[id] = NewSignal();
        }

        return id;
    }

    public string State(Guid jobId)
    {
        lock (_sync)
            return _jobs[jobId];
    }

    /// <summary>Completes when the job's final report (printed or failed) has been answered.</summary>
    public Task JobFinished(Guid jobId)
    {
        lock (_sync)
            return _finished[jobId].Task;
    }

    /// <summary>Completes at the next pending poll answered.</summary>
    public Task NextPoll()
    {
        lock (_sync)
            return _nextPoll.Task;
    }

    public string[] ReportsFor(Guid jobId) =>
        [.. Requests.Where(r => r.Contains(jobId.ToString("D"), StringComparison.Ordinal)).Select(r => r[(r.LastIndexOf('/') + 1)..])];

    public void Dispose()
    {
        HoldClaimAnswer?.Open();
        DropPrintedReports?.Open();
        HoldSetupDownload?.Open();
        _listener.Close();
        try
        {
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath.Trim('/');
        Requests.Enqueue($"{context.Request.HttpMethod} {path}");
        VersionedRequests.Enqueue((context.Request.Headers["X-PrintBridge-Version"] ?? string.Empty, $"{context.Request.HttpMethod} {path}"));
        try
        {
            if (context.Request.Headers["X-PrintBridge-Token"] != Token)
            {
                Write(context, HttpStatusCode.Unauthorized, new { error = "device_auth_invalid" });
                return;
            }

            // WAS-91: a setup link opened while connected asks this server first whether it is for the same restaurant.
            // It is, so the link takes the ordinary exchange.
            if (path == "api/print-bridge/setup/transfer/preview")
            {
                Write(context, HttpStatusCode.OK, new { sameTenant = true });
                return;
            }

            if (path == "api/print-bridge/health")
            {
                Write(context, HttpStatusCode.OK, new { success = true, tenantName = "QA", deviceName = "QA Kasa", serverTimeUtc = DateTime.UtcNow });
                return;
            }

            // WAS-63: the release description and its setup, as Wasla.Web's PrintBridgeApiController serves them.
            if (path == "api/print-bridge/releases/latest")
            {
                if (Release is not { } offered)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    context.Response.Close();
                    return;
                }

                Write(context, HttpStatusCode.OK, new
                {
                    schemaVersion = 1,
                    version = offered.Version,
                    setup = new { fileName = "Wasla.PrintBridge-Setup-x64.exe", bytes = offered.DescribedBytes, sha256 = offered.DescribedSha256 }
                });
                return;
            }

            if (path.StartsWith("api/print-bridge/releases/", StringComparison.Ordinal) && path.EndsWith("/setup", StringComparison.Ordinal))
            {
                if (Release is not { } offered || path != $"api/print-bridge/releases/{offered.Version}/setup")
                {
                    Write(context, HttpStatusCode.NotFound, new { error = "not_found" });
                    return;
                }

                await SendSetupAsync(context, offered.Served);
                return;
            }

            if (path == "api/print-bridge/jobs/pending")
            {
                object[] jobs;
                TaskCompletionSource poll;
                lock (_sync)
                {
                    jobs = [.. _jobs.Where(j => j.Value == "Pending").Select(j => (object)new
                    {
                        id = j.Key,
                        orderId = Guid.NewGuid(),
                        type = "Receipt",
                        copyCount = 1,
                        payloadJson = """{"platform":"Getir","externalOrderCode":"QA-5901"}""",
                        createdAtUtc = DateTime.UtcNow
                    })];
                    poll = _nextPoll;
                    _nextPoll = NewSignal();
                }

                Write(context, HttpStatusCode.OK, new { jobs });
                poll.TrySetResult();
                return;
            }

            var segments = path.Split('/');
            var jobId = segments.Length >= 2 && Guid.TryParse(segments[^2], out var id) ? id : Guid.Empty;
            switch (segments[^1])
            {
                case "mark-printing":
                {
                    var answer = Transition(jobId, "Pending", "Printing");
                    if (HoldClaimAnswer is { } gate)
                    {
                        gate.Entered.TrySetResult();
                        await gate.Opened.Task;
                    }

                    Write(context, HttpStatusCode.OK, answer);
                    return;
                }

                case "mark-printed":
                {
                    if (DropPrintedReports is { } dropped)
                    {
                        // The report arrives but the server never processes it, as when the device dies before the
                        // answer: it is held, then answered 503 once the test lets it go.
                        dropped.Entered.TrySetResult();
                        await dropped.Opened.Task;
                        Write(context, HttpStatusCode.ServiceUnavailable, new { error = "server_error" });
                        return;
                    }

                    Write(context, HttpStatusCode.OK, Transition(jobId, "Printing", "Printed"));
                    Finish(jobId);
                    return;
                }

                case "mark-failed":
                    Write(context, HttpStatusCode.OK, Transition(jobId, "Printing", "Failed"));
                    Finish(jobId);
                    return;

                default:
                    Write(context, HttpStatusCode.NotFound, new { error = "not_found" });
                    return;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // The client went away (the application exited); nothing to answer.
        }
    }

    private object Transition(Guid jobId, string from, string to)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var state))
                return new { success = false, skipped = false, result = "not_found" };
            if (state != from)
                return new { success = false, skipped = true, result = "skipped" };

            _jobs[jobId] = to;
            return new { success = true, skipped = false, result = "ok" };
        }
    }

    private void Finish(Guid jobId)
    {
        lock (_sync)
        {
            if (_finished.TryGetValue(jobId, out var finished))
                finished.TrySetResult();
        }
    }

    private static void Write(HttpListenerContext context, HttpStatusCode status, object body)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private async Task SendSetupAsync(HttpListenerContext context, byte[] setup)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength64 = setup.Length;
        var half = setup.Length / 2;
        await context.Response.OutputStream.WriteAsync(setup.AsMemory(0, half));
        await context.Response.OutputStream.FlushAsync();
        if (HoldSetupDownload is { } hold)
        {
            hold.Entered.TrySetResult();
            await hold.Opened.Task;
        }

        await context.Response.OutputStream.WriteAsync(setup.AsMemory(half));
        context.Response.Close();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>
/// A release the fake server offers (WAS-63): <see cref="Version"/> and its setup. The description is that of
/// <see cref="Setup"/> unless overridden; <see cref="Served"/> is what the download sends (another file, to test tampering).
/// </summary>
internal sealed record TrayFakeRelease(string Version, byte[] Setup)
{
    public long? Bytes { get; init; }

    public string? Sha256 { get; init; }

    public byte[]? ServedSetup { get; init; }

    public long DescribedBytes => Bytes ?? Setup.LongLength;

    public string DescribedSha256 => Sha256 ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Setup)).ToLowerInvariant();

    public byte[] Served => ServedSetup ?? Setup;
}

/// <summary>Holds a step after it started until the test opens it.</summary>
internal sealed class TrayGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => Opened.TrySetResult();
}
