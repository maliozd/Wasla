using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeProtocolUriTests
{
    private const string ValidCode = "abcDEF123456789_-abcDEF1234567890";

    [Fact]
    public void TryParseSetup_AcceptsValidUri()
    {
        var uri = $"wasla-printbridge://setup?server={Uri.EscapeDataString("https://sushim.wasla.local:7200/")}&code={ValidCode}";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("https://sushim.wasla.local:7200/", request!.ServerUrl);
        Assert.Equal(ValidCode, request.Code);
    }

    [Fact]
    public void TryParseSetup_RoundTripsBuiltUri()
    {
        var built = PrintBridgeProtocolUri.BuildSetupUri("https://panel.wasla.com/", ValidCode);

        var ok = PrintBridgeProtocolUri.TryParseSetup(built, out var request, out _);

        Assert.True(ok);
        Assert.Equal("https://panel.wasla.com/", request!.ServerUrl);
        Assert.Equal(ValidCode, request.Code);
    }

    [Fact]
    public void TryParseSetup_RejectsWrongScheme()
    {
        var uri = $"orderhub-printbridge://setup?server={Uri.EscapeDataString("https://x.example.com")}&code={ValidCode}";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("scheme", error);
    }

    [Fact]
    public void TryParseSetup_RejectsWrongAction()
    {
        var uri = $"wasla-printbridge://configure?server={Uri.EscapeDataString("https://x.example.com")}&code={ValidCode}";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.Equal("action", error);
    }

    [Fact]
    public void TryParseSetup_RejectsMissingCode()
    {
        var uri = $"wasla-printbridge://setup?server={Uri.EscapeDataString("https://x.example.com")}";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.Equal("missing_code", error);
    }

    [Theory]
    [InlineData("ftp://x.example.com")]
    [InlineData("file:///c:/temp")]
    public void TryParseSetup_RejectsNonHttpServerUrl(string server)
    {
        var uri = $"wasla-printbridge://setup?server={Uri.EscapeDataString(server)}&code={ValidCode}";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.Equal("invalid_server_url", error);
    }

    [Theory]
    [InlineData("not a uri at all")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseSetup_RejectsMalformedOrEmpty(string uri)
    {
        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseSetup_RejectsUnexpectedParameter()
    {
        var uri = $"wasla-printbridge://setup?server={Uri.EscapeDataString("https://x.example.com")}&code={ValidCode}&token=secret";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.Equal("unexpected_param", error);
    }

    [Fact]
    public void TryParseSetup_RejectsOversizedUri()
    {
        var hugeCode = new string('a', PrintBridgeProtocolUri.MaxUriLength + 10);
        var uri = $"wasla-printbridge://setup?server={Uri.EscapeDataString("https://x.example.com")}&code={hugeCode}";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.Equal("too_long", error);
    }

    [Fact]
    public void TryParseSetup_RejectsInvalidCodeCharacters()
    {
        var badCode = "abc$def%ghi*jkl/mno+pqr";
        var uri = $"wasla-printbridge://setup?server={Uri.EscapeDataString("https://sushim.wasla.local")}&code={Uri.EscapeDataString(badCode)}";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.Equal("invalid_code", error);
    }

    [Fact]
    public void TryParseSetup_RejectsArbitraryHttpsServerUrl()
    {
        var uri = $"wasla-printbridge://setup?server={Uri.EscapeDataString("https://evil.example.com")}&code={ValidCode}";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.Equal("untrusted_server_url", error);
    }

    [Fact]
    public void TryParseSetup_RejectsTooShortCode()
    {
        var uri = $"wasla-printbridge://setup?server={Uri.EscapeDataString("https://sushim.wasla.local")}&code=short";

        var ok = PrintBridgeProtocolUri.TryParseSetup(uri, out _, out var error);

        Assert.False(ok);
        Assert.Equal("invalid_code", error);
    }
}
