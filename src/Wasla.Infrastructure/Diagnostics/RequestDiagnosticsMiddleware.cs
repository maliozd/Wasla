using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Diagnostics;

public sealed class RequestDiagnosticsMiddleware
{
    public const string TraceHeaderName = "X-Trace-Id";

    private readonly RequestDelegate _next;
    private readonly bool _logRequestCompletion;

    public RequestDiagnosticsMiddleware(RequestDelegate next, IOptions<RequestDiagnosticsOptions> options)
    {
        _next = next;
        _logRequestCompletion = options.Value.LogRequestCompletion;
    }

    public async Task InvokeAsync(HttpContext context, ILogger<RequestDiagnosticsMiddleware> logger)
    {
        var state = RequestLogState.Capture(context);
        context.Items[RequestLogState.ItemKey] = state;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[TraceHeaderName] = state.TraceId;
            return Task.CompletedTask;
        });

        var start = Stopwatch.GetTimestamp();
        using (logger.BeginScope(state))
        {
            try
            {
                await _next(context);
            }
            finally
            {
                if (_logRequestCompletion)
                    LogCompletion(logger, context, state, Stopwatch.GetElapsedTime(start));
            }
        }
    }

    private static void LogCompletion(
        ILogger logger,
        HttpContext context,
        RequestLogState state,
        TimeSpan elapsed)
    {
        var statusCode = context.Response.StatusCode;
        var path = context.Request.Path;
        var level = statusCode >= 500
            ? LogLevel.Warning
            : RequestDiagnosticPaths.LogCompletionAtDebug(path, statusCode)
                ? LogLevel.Debug
                : LogLevel.Information;

        logger.Log(
            level,
            "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs} ms TraceId={TraceId} TenantId={TenantId}",
            context.Request.Method,
            path.Value ?? "/",
            statusCode,
            (long)elapsed.TotalMilliseconds,
            state.TraceId,
            state.TenantId);
    }
}
