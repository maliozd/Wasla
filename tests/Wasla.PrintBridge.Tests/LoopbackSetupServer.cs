using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// A real HTTP server on a free loopback port, so that setup requests (WAS-91) and device requests (WAS-191) go through
/// the real production handler over a socket. Every request that arrives is recorded with its method, path, token and
/// body. Requests are forwarded to <c>forward</c> (the fake Wasla API) when one is given, and answered <c>{}</c>
/// otherwise. A <see cref="Redirect"/> answers chosen paths with a redirect instead: before forwarding (the API never saw
/// the request) or after it (the API processed it).
/// </summary>
internal sealed class LoopbackSetupServer : IDisposable
{
    private static readonly string[] ForwardedHeaders =
        ["X-PrintBridge-Token", "X-PrintBridge-Installation-Id", "X-PrintBridge-Version", "X-PrintBridge-Name"];

    private readonly HttpListener _listener = new();
    private readonly HttpMessageInvoker? _forward;
    private readonly Task _serving;
    private LoopbackRedirect? _redirect;
    private int _redirected;

    public LoopbackSetupServer(HttpMessageHandler? forward = null)
    {
        Url = $"http://localhost:{FreePort()}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _forward = forward is null ? null : new HttpMessageInvoker(forward, disposeHandler: false);
        _serving = Task.Run(ServeAsync);
    }

    /// <summary>The origin, without a trailing slash.</summary>
    public string Url { get; }

    public ConcurrentQueue<LoopbackRequest> Requests { get; } = new();

    /// <summary>Setting it starts counting <see cref="LoopbackRedirect.Times"/> again.</summary>
    public LoopbackRedirect? Redirect
    {
        get => Volatile.Read(ref _redirect);
        set
        {
            Volatile.Write(ref _redirected, 0);
            Volatile.Write(ref _redirect, value);
        }
    }

    public int Count(string path) => Requests.Count(r => r.Path == path);

    public void Dispose()
    {
        _listener.Close();
        try
        {
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _forward?.Dispose();
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
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url!.AbsolutePath.Trim('/');
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            Requests.Enqueue(new LoopbackRequest(request.HttpMethod, path, request.Headers["X-PrintBridge-Token"], body));

            var redirect = Redirect;
            var redirects = redirect is not null
                && (redirect.Path is null || redirect.Path == path)
                && (redirect.Times is null || Interlocked.Increment(ref _redirected) <= redirect.Times);
            if (redirects && !redirect!.AfterForwarding)
            {
                WriteRedirect(response, redirect, path);
                return;
            }

            if (_forward is null)
            {
                await WriteAsync(response, 200, "application/json", Encoding.UTF8.GetBytes("{}"), errorCode: null);
                return;
            }

            using var forwarded = new HttpRequestMessage(new HttpMethod(request.HttpMethod), $"{Url}/{path}");
            foreach (var header in ForwardedHeaders)
            {
                if (request.Headers[header] is { } value)
                    forwarded.Headers.TryAddWithoutValidation(header, value);
            }

            if (body.Length > 0)
                forwarded.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var answer = await _forward.SendAsync(forwarded, CancellationToken.None);
            if (redirects)
            {
                WriteRedirect(response, redirect!, path);
                return;
            }

            await WriteAsync(
                response,
                (int)answer.StatusCode,
                answer.Content.Headers.ContentType?.ToString(),
                await answer.Content.ReadAsByteArrayAsync(),
                answer.Headers.TryGetValues("X-PrintBridge-Error-Code", out var codes) ? codes.FirstOrDefault() : null);
        }
        catch (Exception)
        {
            response.StatusCode = 500;
        }
        finally
        {
            response.Close();
        }
    }

    private static void WriteRedirect(HttpListenerResponse response, LoopbackRedirect redirect, string path)
    {
        response.StatusCode = redirect.Status;
        response.RedirectLocation = $"{redirect.Location.TrimEnd('/')}/{path}";
        if (redirect.ErrorCode is null)
        {
            response.ContentLength64 = 0;
            return;
        }

        // A redirect that also looks like a Print Bridge API rejection, in the header and the body.
        var body = Encoding.UTF8.GetBytes($$"""{"error":"{{redirect.ErrorCode}}"}""");
        response.Headers["X-PrintBridge-Error-Code"] = redirect.ErrorCode;
        response.ContentType = "application/json";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string? contentType, byte[] bytes, string? errorCode)
    {
        response.StatusCode = status;
        if (contentType is not null)
            response.ContentType = contentType;
        if (errorCode is not null)
            response.Headers["X-PrintBridge-Error-Code"] = errorCode;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>A request as it arrived: method, path without the query, the device token header and the body.</summary>
internal sealed record LoopbackRequest(string Method, string Path, string? Token, string Body);

/// <summary>
/// Answer <paramref name="Path"/> (every path when null) with <paramref name="Status"/> and a <c>Location</c> on
/// <paramref name="Location"/> with the same path; after forwarding the request when <paramref name="AfterForwarding"/>.
/// Only the first <paramref name="Times"/> matching requests when given; with <paramref name="ErrorCode"/> as a Print
/// Bridge error code in the header and the body.
/// </summary>
internal sealed record LoopbackRedirect(
    int Status,
    string Location,
    string? Path = null,
    bool AfterForwarding = false,
    int? Times = null,
    string? ErrorCode = null);
