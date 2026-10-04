using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeSetupCertificatePolicyTests
{
    private const string ValidCode = "abcDEF123456789_-abcDEF1234567890";

    [Theory]
    [InlineData("https://localhost:7200/")]
    [InlineData("https://127.0.0.1:7200/")]
    [InlineData("https://[::1]:7200/")]
    [InlineData("https://wasla.local:7200/")]
    [InlineData("https://sushi-m.wasla.local:7200/")]
    public void LocalDevelopmentOrigins_DisableRevocationCheck(string serverUrl)
    {
        var uri = new Uri(serverUrl);

        Assert.True(PrintBridgeSetupCertificatePolicy.ShouldDisableRevocationCheck(uri));

        using var handler = new PrintBridgeSetupHttpClientFactory().CreateHandler(uri);
        Assert.False(handler.CheckCertificateRevocationList);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Theory]
    [InlineData("https://evilwasla.local:7200/")]
    [InlineData("https://wasla.local.evil.com:7200/")]
    [InlineData("https://panel.wasla.com/")]
    [InlineData("https://example.com/")]
    public void NonLocalDevelopmentOrigins_KeepRevocationCheckEnabled(string serverUrl)
    {
        var uri = new Uri(serverUrl);

        Assert.False(PrintBridgeSetupCertificatePolicy.ShouldDisableRevocationCheck(uri));

        using var handler = new PrintBridgeSetupHttpClientFactory().CreateHandler(uri);
        Assert.True(handler.CheckCertificateRevocationList);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void TrustedOriginValidation_RemainsUnchanged()
    {
        var localUri = BuildSetupUri("https://sushi-m.wasla.local:7200/");
        var productionUri = BuildSetupUri("https://panel.wasla.com/");
        var rejectedUri = BuildSetupUri("https://evil.example.com/");

        Assert.True(PrintBridgeProtocolUri.TryParseSetup(localUri, out _, out var localError));
        Assert.Null(localError);

        Assert.True(PrintBridgeProtocolUri.TryParseSetup(productionUri, out _, out var productionError));
        Assert.Null(productionError);

        Assert.False(PrintBridgeProtocolUri.TryParseSetup(rejectedUri, out _, out var rejectedError));
        Assert.Equal("untrusted_server_url", rejectedError);
    }

    private static string BuildSetupUri(string serverUrl) =>
        $"wasla-printbridge://setup?server={Uri.EscapeDataString(serverUrl)}&code={ValidCode}";
}
