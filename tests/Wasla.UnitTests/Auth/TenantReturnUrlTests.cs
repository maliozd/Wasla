using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// The shape rules a return URL must pass before its destination is authorized (WAS-113). Whether the user may open
/// the page is checked separately, over HTTP, in <see cref="TenantPostLoginRedirectTests"/>.
/// </summary>
public sealed class TenantReturnUrlTests
{
    [Theory]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/orders?status=New&page=2", "/orders")]
    [InlineData("/orders/details/6f1d5b0e-1c1d-4c55-9a40-3c1c8b8f4f11", "/orders/details/6f1d5b0e-1c1d-4c55-9a40-3c1c8b8f4f11")]
    [InlineData("/settings/users/", "/settings/users/")]
    [InlineData("/orders?returnUrl=%2F%2Fevil.example", "/orders")]
    [InlineData("/%61uth-help", "/auth-help")]
    public void PlainLocalPaths_AreAccepted_WithTheDecodedPathToMatch(string returnUrl, string path)
    {
        Assert.True(TenantReturnUrl.TryGetLocalPath(returnUrl, out var parsed));
        Assert.Equal(path, parsed.Value);
    }

    [Theory]
    // Missing or too long
    [InlineData(null)]
    [InlineData("")]
    // Other hosts and schemes
    [InlineData("https://evil.example/")]
    [InlineData("HtTpS://evil.example")]
    [InlineData("https:/evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("mailto:a@b.example")]
    // Protocol-relative and backslash forms
    [InlineData("//evil.example")]
    [InlineData("///evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("\\/evil.example")]
    [InlineData("/orders\\..\\dashboard")]
    // Encoded and double-encoded separators
    [InlineData("%2F%2Fevil.example")]
    [InlineData("/%2F%2Fevil.example")]
    [InlineData("/%2fevil.example")]
    [InlineData("/%5Cevil.example")]
    [InlineData("/%5cevil.example")]
    [InlineData("/%252F%252Fevil.example")]
    [InlineData("/%25")]
    // Malformed escapes, dot segments, encoded control characters
    [InlineData("/orders%")]
    [InlineData("/orders%zz")]
    [InlineData("/orders/%2e%2e/settings/users")]
    [InlineData("/orders/../settings/users")]
    [InlineData("/./dashboard")]
    [InlineData("/orders/%0d%0aSet-Cookie:x")]
    [InlineData("/orders%00")]
    // Raw white space, control and non-ASCII characters, fragments, app-relative and relative forms
    [InlineData(" /orders")]
    [InlineData("/orders ")]
    [InlineData("/ord ers")]
    [InlineData("/orders\r\nSet-Cookie: x=1")]
    [InlineData("/orders\t")]
    [InlineData("/orders\u0000")]
    [InlineData("/orders/ç")]
    [InlineData("/orders#top")]
    [InlineData("~/orders")]
    [InlineData("orders")]
    [InlineData("?x=1")]
    // The sign-in pages, which would loop
    [InlineData("/auth")]
    [InlineData("/auth/")]
    [InlineData("/auth/login")]
    [InlineData("/AUTH/LOGIN?ReturnUrl=%2Fauth%2Flogin")]
    [InlineData("/auth/access-denied")]
    [InlineData("/auth/logout")]
    [InlineData("/%61uth/login")]
    public void UnsafeValues_AreRefused(string? returnUrl)
    {
        Assert.False(TenantReturnUrl.TryGetLocalPath(returnUrl, out var path));
        Assert.False(path.HasValue);
    }

    [Fact]
    public void OverlongValues_AreRefused()
    {
        Assert.True(TenantReturnUrl.TryGetLocalPath("/orders?q=" + new string('a', 2038), out _));
        Assert.False(TenantReturnUrl.TryGetLocalPath("/orders?q=" + new string('a', 2039), out _));
    }
}
