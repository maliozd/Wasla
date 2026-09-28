using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Diagnostics;

namespace Wasla.UnitTests.Diagnostics;

public sealed class RequestDiagnosticsTests
{
    [Fact]
    public async Task CompletionLog_OmitsQueryString_AndIncludesTenantAndTrace()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var logger = new CollectingLogger<RequestDiagnosticsMiddleware>();
        var middleware = new RequestDiagnosticsMiddleware(
            context =>
            {
                var state = Assert.IsType<RequestLogState>(context.Items[RequestLogState.ItemKey]);
                state.SetTenantId(tenantId);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            Options.Create(new RequestDiagnosticsOptions()));
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/orders";
        http.Request.QueryString = new QueryString("?token=super-secret-value&phone=5551112233");

        await middleware.InvokeAsync(http, logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("GET", entry.Message, StringComparison.Ordinal);
        Assert.Contains("/orders", entry.Message, StringComparison.Ordinal);
        Assert.Contains("204", entry.Message, StringComparison.Ordinal);
        Assert.Contains(tenantId.ToString("D"), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("5551112233", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", entry.Message, StringComparison.Ordinal);
        Assert.Contains("TraceId=", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HighFrequencySuccess_IsDebug_AndFailedHealthProbe_IsWarning()
    {
        var logger = new CollectingLogger<RequestDiagnosticsMiddleware>();
        var middleware = new RequestDiagnosticsMiddleware(
            context =>
            {
                context.Response.StatusCode = context.Request.Path == "/health/ready"
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            Options.Create(new RequestDiagnosticsOptions()));

        var poll = new DefaultHttpContext();
        poll.Request.Method = "GET";
        poll.Request.Path = "/orders/table";
        await middleware.InvokeAsync(poll, logger);

        var health = new DefaultHttpContext();
        health.Request.Method = "GET";
        health.Request.Path = "/health/ready";
        await middleware.InvokeAsync(health, logger);

        Assert.Equal(LogLevel.Debug, logger.Entries[0].Level);
        Assert.Equal(LogLevel.Warning, logger.Entries[1].Level);
        Assert.Contains("/health/ready", logger.Entries[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestLogState_AddsTenantAfterCapture_AndRejectsUnsafeTraceTokens()
    {
        Assert.Equal("unknown", RequestLogState.SanitizeTraceToken("bad\r\nX-Injected: yes"));
        Assert.Equal("abc123", RequestLogState.SanitizeTraceToken("abc123"));

        var http = new DefaultHttpContext();
        http.TraceIdentifier = "safe-trace";
        var captured = RequestLogState.Capture(http);
        var tenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        captured.SetTenantId(tenantId);

        Assert.Contains(captured, pair => pair.Key == "TenantId" && (string)pair.Value == tenantId.ToString("D"));
    }
}

public sealed class CollectingLogger<T> : ILogger<T>
{
    public List<CollectedLog> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new CollectedLog(logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}

public sealed record CollectedLog(LogLevel Level, string Message, Exception? Exception);
