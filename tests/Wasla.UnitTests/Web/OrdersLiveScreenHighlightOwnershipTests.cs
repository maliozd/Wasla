namespace Wasla.UnitTests.Web;

/// <summary>
/// Phase 2B2: Live Screen owns operational new-order highlight; Orders management does not.
/// </summary>
public sealed class OrdersLiveScreenHighlightOwnershipTests
{
    [Fact]
    public void Orders_DoesNotOwnOperationalNewOrderHighlightOnPoll()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        Assert.DoesNotContain("if (live && newIds.length > 0)", tableJs, StringComparison.Ordinal);
        Assert.Contains("if (isLiveDisplayPage() && newIds.length > 0)", tableJs, StringComparison.Ordinal);
        Assert.Contains("markOrdersAsRecentlyNew(newIds)", tableJs, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(tableJs, "markOrdersAsRecentlyNew(newIds)"));
    }

    [Fact]
    public void LiveScreen_BaselineDoesNotMarkExistingCardsAsRecentlyNew()
    {
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        Assert.Contains("O.table.captureKnownOrderIdsFromContainer()", liveJs, StringComparison.Ordinal);
        Assert.DoesNotContain("markOrdersAsRecentlyNew", liveJs, StringComparison.Ordinal);
        Assert.True(
            liveJs.IndexOf("captureKnownOrderIdsFromContainer", StringComparison.Ordinal)
            < liveJs.IndexOf("initPolling", StringComparison.Ordinal));
        Assert.Contains("isLiveDisplayPage() && newIds.length > 0", tableJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_NewIdsReceiveHighlightAfterPollRender()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        var markIdx = tableJs.IndexOf("markOrdersAsRecentlyNew(newIds)", StringComparison.Ordinal);
        var applyAfterMark = tableJs.IndexOf("applyNewOrderVisualState()", markIdx, StringComparison.Ordinal);
        var innerHtmlIdx = tableJs.IndexOf("container.innerHTML = html", StringComparison.Ordinal);
        Assert.True(innerHtmlIdx >= 0 && markIdx > innerHtmlIdx && applyAfterMark > markIdx);

        Assert.Contains("applyHighlightClassesToRow(row)", tableJs, StringComparison.Ordinal);
        Assert.Contains("order-row-new", tableJs, StringComparison.Ordinal);
        Assert.Contains("scheduleNewOrderHighlightCleanup", tableJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_MultipleNewIdsShareOneMarkBatch()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        Assert.Equal(1, CountOccurrences(tableJs, "markOrdersAsRecentlyNew(newIds)"));
        Assert.Contains("orderIds.forEach(function (id)", tableJs, StringComparison.Ordinal);
        Assert.DoesNotContain("for (let i = 0; i < newIds.length", tableJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_UnchangedIdsAreNotReMarkedWithoutNewDetection()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");

        Assert.Contains("const newIds = detectNewOrderIds(ids);", tableJs, StringComparison.Ordinal);
        Assert.Contains("if (isLiveDisplayPage() && newIds.length > 0)", tableJs, StringComparison.Ordinal);
        Assert.Contains("captureKnownOrderIdsFromContainer();", tableJs, StringComparison.Ordinal);
        // Highlight only when newIds exist; known set update prevents re-detection next poll.
        var liveHighlightBlock = tableJs[
            tableJs.IndexOf("// Phase 2B2:", StringComparison.Ordinal)
            ..tableJs.IndexOf("// Phase 2B1:", StringComparison.Ordinal)];
        Assert.Contains("markOrdersAsRecentlyNew(newIds)", liveHighlightBlock, StringComparison.Ordinal);
        Assert.Contains("newIds.length > 0", liveHighlightBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_HasSubtleCardHighlightStylesWithoutAggressiveAnimation()
    {
        var css = ReadWwwroot("css", "orderhub-theme.css");
        var partial = Read("Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml");

        Assert.Contains("data-order-id=\"@o.Id\"", partial, StringComparison.Ordinal);
        Assert.Contains("oh-live-screen-card", partial, StringComparison.Ordinal);
        Assert.Contains("data-new-badge", partial, StringComparison.Ordinal);
        Assert.Contains(".oh-live-screen-card.order-row-new", css, StringComparison.Ordinal);
        Assert.Contains("animation: none !important", css, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
