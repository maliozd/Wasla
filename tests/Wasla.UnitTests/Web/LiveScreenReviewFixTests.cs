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
    public void LiveScreen_UsesTryCurrencyIndependentOfUiCulture()
    {
        var display = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");
        var format = Slice(storeJs, "function liveMoneyFormat", "function elapsedMinutes");
        var money = Slice(storeJs, "function formatMoney", "function ensureDateFormatters");

        Assert.DoesNotContain("RegionInfo", display, StringComparison.Ordinal);
        Assert.DoesNotContain("currencyCode", display, StringComparison.Ordinal);
        Assert.DoesNotContain("currencyCode", storeJs, StringComparison.Ordinal);
        Assert.Contains("style: \"currency\"", format, StringComparison.Ordinal);
        Assert.Contains("currency: \"TRY\"", format, StringComparison.Ordinal);
        Assert.Contains("currencyDisplay: \"narrowSymbol\"", format, StringComparison.Ordinal);
        Assert.DoesNotContain("currencyDisplay: \"symbol\"", format, StringComparison.Ordinal);
        Assert.Contains("new Intl.NumberFormat(locale", format, StringComparison.Ordinal);
        Assert.DoesNotContain("currency: culture", format, StringComparison.Ordinal);
        Assert.DoesNotContain("currency: locale", format, StringComparison.Ordinal);
        Assert.Contains("return api.formatAmount(amount, culture())", money, StringComparison.Ordinal);
        Assert.Contains("return formatMoney(amount)", money, StringComparison.Ordinal);
        Assert.Contains("formatListMoney(order.totalAmount)", storeJs, StringComparison.Ordinal);
        Assert.Contains("formatMoney(order.totalAmount)", storeJs, StringComparison.Ordinal);
        Assert.Contains(".wasla-live-detail__grand dd", storeJs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("ru-RU")]
    public void LiveScreenCurrency_StaysTryForEveryUiCulture(string cultureName)
    {
        var format = Slice(
            ReadWwwroot("js", "orders", "orders-live-store.js"),
            "function liveMoneyFormat",
            "function elapsedMinutes");

        Assert.Contains("currency: \"TRY\"", format, StringComparison.Ordinal);
        Assert.Contains("currencyDisplay: \"narrowSymbol\"", format, StringComparison.Ordinal);
        Assert.DoesNotContain("currency: \"" + cultureName + "\"", format, StringComparison.Ordinal);
        Assert.DoesNotContain("USD", format, StringComparison.Ordinal);
        Assert.DoesNotContain("EUR", format, StringComparison.Ordinal);
        Assert.DoesNotContain("SAR", format, StringComparison.Ordinal);
        Assert.DoesNotContain("RUB", format, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot(), "src", "Wasla.Web" }.Concat(segments).ToArray()));

    private static string ReadWwwroot(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot(), "src", "Wasla.Web", "wwwroot" }.Concat(segments).ToArray()));

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        return source.Substring(startIndex, endIndex - startIndex);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
