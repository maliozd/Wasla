using System.Globalization;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Source contracts for the LS-3 review fixes.
/// These read the current files; they do not execute the browser.
/// </summary>
public sealed class LiveScreenReviewFixTests
{
    [Fact]
    public void LiveScreen_FetchesThroughOneResponseClassifier()
    {
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");
        var fetchStart = storeJs.IndexOf("async function fetchSnapshot", StringComparison.Ordinal);
        var fetchEnd = storeJs.IndexOf("async function onAccepted", fetchStart, StringComparison.Ordinal);
        var fetch = storeJs.Substring(fetchStart, fetchEnd - fetchStart);

        Assert.Contains("classifyResponse(response, text, JSON.parse)", fetch, StringComparison.Ordinal);
        Assert.DoesNotContain("text/html", fetch, StringComparison.Ordinal);
        Assert.DoesNotContain("kind: \"session\"", fetch, StringComparison.Ordinal);
        Assert.Contains("isLoginRedirect(response)", storeJs, StringComparison.Ordinal);
        Assert.Contains("pendingMutations", storeJs, StringComparison.Ordinal);
        Assert.Contains("if (typeof endMutation === \"function\") endMutation();", ReadWwwroot("js", "orders", "orders-actions.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_DoesNotSelectCurrencyFromTheUiCulture()
    {
        var display = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var cards = Read("Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        Assert.DoesNotContain("RegionInfo", display, StringComparison.Ordinal);
        Assert.DoesNotContain("currencyCode", display, StringComparison.Ordinal);
        Assert.Contains("ToString(\"N2\", CultureInfo.CurrentCulture)", cards, StringComparison.Ordinal);
        Assert.DoesNotContain("ToString(\"C\"", cards, StringComparison.Ordinal);
        Assert.DoesNotContain("style: \"currency\"", storeJs, StringComparison.Ordinal);
        Assert.DoesNotContain("currencyCode", storeJs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("ru-RU")]
    public void LiveScreenAmount_UsesCultureNumberFormatWithoutACurrencyCode(string cultureName)
    {
        var formatted = 120.5m.ToString("N2", CultureInfo.GetCultureInfo(cultureName));

        Assert.DoesNotContain("₺", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("$", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("SAR", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRY", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USD", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("120", formatted, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot(), "src", "Wasla.Web" }.Concat(segments).ToArray()));

    private static string ReadWwwroot(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot(), "src", "Wasla.Web", "wwwroot" }.Concat(segments).ToArray()));

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
