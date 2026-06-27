using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Web.Middleware;

namespace Wasla.UnitTests.Printing;

public sealed class PrintBridgeAuthMiddlewareTests
{
    [Fact]
    public async Task InvalidTokenResponse_IncludesStableErrorCodeWithoutEchoingSecrets()
    {
        const string rawToken = "raw-secret-token";
        const string tokenHash = "hashed-secret-token";
        const string setupCode = "setup-secret-code";
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/print-bridge/jobs/pending";
        context.Request.Headers[PrintBridgeAuthMiddleware.TokenHeader] = rawToken;
        context.Response.Body = new MemoryStream();
        var middleware = new PrintBridgeAuthMiddleware(_ => throw new InvalidOperationException("Should not call next."));

        await middleware.InvokeAsync(
            context,
            new FakePrintBridgeAuthService(PrintBridgeAuthFailureCode.DeviceRemoved),
            NullLogger<PrintBridgeAuthMiddleware>.Instance);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("device_removed", context.Response.Headers[PrintBridgeAuthMiddleware.ErrorCodeHeader]);
        Assert.Contains("\"error\":\"device_removed\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(tokenHash, body, StringComparison.Ordinal);
        Assert.DoesNotContain(setupCode, body, StringComparison.Ordinal);
    }

    private sealed class FakePrintBridgeAuthService : IPrintBridgeAuthService
    {
        private readonly PrintBridgeAuthFailureCode _failureCode;

        public FakePrintBridgeAuthService(PrintBridgeAuthFailureCode failureCode)
        {
            _failureCode = failureCode;
        }

        public Task<PrintBridgeAuthContext?> AuthenticateAsync(
            string rawToken,
            PrintBridgeClientInfo clientInfo,
            CancellationToken ct) =>
            Task.FromResult<PrintBridgeAuthContext?>(null);

        public Task<PrintBridgeAuthResult> AuthenticateDetailedAsync(
            string rawToken,
            PrintBridgeClientInfo clientInfo,
            CancellationToken ct) =>
            Task.FromResult(PrintBridgeAuthResult.Failure(_failureCode));
    }
}
