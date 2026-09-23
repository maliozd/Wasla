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
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        var refreshStart = tableJs.IndexOf("async function refreshOrdersTable", StringComparison.Ordinal);
        var refreshEnd = tableJs.IndexOf("function initPolling", refreshStart, StringComparison.Ordinal);
        var refresh = tableJs.Substring(refreshStart, refreshEnd - refreshStart);
        Assert.DoesNotContain("markOrdersAsRecentlyNew(", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("playSoundNow", refresh, StringComparison.Ordinal);
        Assert.Contains("O.table.markOrdersAsRecentlyNew(meta.newIds)", storeJs, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(storeJs, "O.table.markOrdersAsRecentlyNew(meta.newIds)"));
    }

    [Fact]
    public void LiveScreen_BaselineDoesNotMarkExistingCardsAsRecentlyNew()
    {
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        Assert.Contains("O.liveStore.start()", liveJs, StringComparison.Ordinal);
        Assert.DoesNotContain("markOrdersAsRecentlyNew", liveJs, StringComparison.Ordinal);
        Assert.Contains("if (!baselineReady)", storeJs, StringComparison.Ordinal);
        Assert.Contains("isBaseline || !meta.newIds", storeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_NewIdsReceiveHighlightAfterPollRender()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        var renderIdx = storeJs.IndexOf("diff = renderSnapshot(snapshot);", StringComparison.Ordinal);
        var markIdx = storeJs.IndexOf("O.table.markOrdersAsRecentlyNew(meta.newIds)", StringComparison.Ordinal);
        var applyAfterMark = storeJs.IndexOf("O.table.applyNewOrderVisualState()", markIdx, StringComparison.Ordinal);
        Assert.True(renderIdx >= 0 && markIdx > renderIdx && applyAfterMark > markIdx);

        Assert.Contains("applyHighlightClassesToRow(row)", tableJs, StringComparison.Ordinal);
        Assert.Contains("order-row-new", tableJs, StringComparison.Ordinal);
        Assert.Contains("scheduleNewOrderHighlightCleanup", tableJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_MultipleNewIdsShareOneMarkBatch()
    {
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        Assert.Equal(1, CountOccurrences(storeJs, "O.table.markOrdersAsRecentlyNew(meta.newIds)"));
        Assert.Contains("orderIds.forEach(function (id)", tableJs, StringComparison.Ordinal);
        Assert.DoesNotContain("meta.newIds.forEach", storeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_UnchangedIdsAreNotReMarkedWithoutNewDetection()
    {
        var storeJs = ReadWwwroot("js", "orders", "orders-live-store.js");

        Assert.Contains("const newIds = collectNewIds(known, result.snapshot.orders, baselineReady);", storeJs, StringComparison.Ordinal);
        Assert.Contains("if (meta.isBaseline || !meta.newIds || !meta.newIds.length) return;", storeJs, StringComparison.Ordinal);
        Assert.Contains("if (accepted && accepted.accepted === false)", storeJs, StringComparison.Ordinal);
        var acceptIndex = storeJs.IndexOf("options.onAccepted(result.snapshot", StringComparison.Ordinal);
        var rememberIndex = storeJs.IndexOf("rememberOrderIds(known, result.snapshot.orders)", StringComparison.Ordinal);
        Assert.True(acceptIndex >= 0 && rememberIndex > acceptIndex);
    }

    [Fact]
    public void LiveScreen_HasSubtleCardHighlightStylesWithoutAggressiveAnimation()
    {
        var css = ReadWwwroot("css", "wasla-theme.css");
        var partial = Read("Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml");

        Assert.Contains("data-order-id=\"@o.Id\"", partial, StringComparison.Ordinal);
        Assert.Contains("wasla-live-screen-card", partial, StringComparison.Ordinal);
        Assert.Contains("data-new-badge", partial, StringComparison.Ordinal);
        Assert.Contains(".wasla-live-screen-card.order-row-new", css, StringComparison.Ordinal);
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
