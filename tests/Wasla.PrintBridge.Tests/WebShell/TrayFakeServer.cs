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
/// claim can be held with <see cref="HoldClaimAnswer"/>, so a job is deterministically active while the test acts. A setup
/// link pointing here exchanges its code for this server's address and the same fake token.
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

    /// <summary>When set, the claim (mark-printing) is processed and then its answer is held until the gate opens.</summary>
    public TrayGate? HoldClaimAnswer { get; set; }

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
        try
        {
            // A setup link: the one-time code is exchanged for this server's address and fake token.
            if (path == "api/print-bridge/setup/exchange")
            {
                Write(context, HttpStatusCode.OK, new
                {
                    sessionId = Guid.NewGuid(),
                    serverUrl = Url,
                    deviceToken = Token,
                    deviceName = "QA Kasa",
                    installationId = Guid.NewGuid(),
                    completionCredential = "completion-not-a-real-credential"
                });
                return;
            }

            if (path == "api/print-bridge/setup/complete")
            {
                Write(context, HttpStatusCode.OK, new { success = true });
                return;
            }

            if (context.Request.Headers["X-PrintBridge-Token"] != Token)
            {
                Write(context, HttpStatusCode.Unauthorized, new { error = "device_auth_invalid" });
                return;
            }

            if (path == "api/print-bridge/health")
            {
                Write(context, HttpStatusCode.OK, new { success = true, customerName = "QA", deviceName = "QA Kasa", serverTimeUtc = DateTime.UtcNow });
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
                    Write(context, HttpStatusCode.OK, Transition(jobId, "Printing", "Printed"));
                    Finish(jobId);
                    return;

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

/// <summary>Holds a step after it started until the test opens it.</summary>
internal sealed class TrayGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => Opened.TrySetResult();
}
