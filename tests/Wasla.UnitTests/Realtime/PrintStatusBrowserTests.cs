using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Web;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-23 end to end in a real headless Chromium browser against <see cref="TenantVisualWebHost"/> (real controllers,
/// views, scripts, the vendored SignalR client and the hub over a WebSocket). Receipt jobs are created and reported
/// through the real print services on the server, as a manual print, the Worker or Print Bridge would; the open page
/// shows Pending, Printing and the final outcome without a reload. With the live connection stopped, the open detail
/// modal falls back to reading the state every 10 seconds, and a reconnect reads it at once. Skips only when no
/// Chromium browser is installed or Bootstrap cannot be loaded from its CDN.
/// </summary>
public sealed class PrintStatusBrowserTests : IAsyncLifetime
{
    private static readonly TimeSpan Push = TimeSpan.FromSeconds(5);
    private TenantVisualWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantVisualWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static Guid Tenant => TenantVisualWebHost.TenantId;
    private static Guid Order => TenantVisualWebHost.DetailOrderId;

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    public async Task TheOrderDetailsPage_ShowsEachPrintStatus_AsItHappens(string culture)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await _host.BrowserAsync(culture, 1280, 900, mobile: false, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser print status checks.");
        await LoadAsync(browser, $"/orders/details/{Order}", ct);
        await WaitConnectedAsync(browser, ct);
        await browser.EvaluateAsync<bool>("(window.__noReload = true)", ct);

        var initial = await ReadActionAsync(browser, ct);
        Assert.Equal(("print", Resource(culture, "Orders.Print.Action"), false, ""), initial);

        await CreateReceiptAsync(ct);
        await WaitForAsync(browser, "queued", ct);
        Assert.Equal(("queued", Resource(culture, "Orders.Print.InQueue"), true, ""), await ReadActionAsync(browser, ct));

        var jobId = await JobIdAsync(ct);
        await BridgeAsync(bridge => bridge.TryMarkPrintingAsync(Tenant, jobId, "Kitchen", null, ct));
        await WaitForAsync(browser, "printing", ct);
        Assert.Equal(("printing", Resource(culture, "Orders.Print.Printing"), true, ""), await ReadActionAsync(browser, ct));

        await BridgeAsync(bridge => bridge.TryMarkPrintedAsync(Tenant, jobId, ct));
        await WaitForAsync(browser, "reprint", ct);
        Assert.Equal(
            ("reprint", Resource(culture, "PrintBridge.Reprint"), false, Resource(culture, "Orders.Print.Status.Printed")),
            await ReadActionAsync(browser, ct));

        Assert.True(await browser.EvaluateAsync<bool>("window.__noReload === true", ct), "The page was reloaded.");
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", await browser.EvaluateAsync<string>("document.documentElement.getAttribute('dir')", ct));
        Assert.Null(_host.ServerErrors);
    }

    [Fact]
    public async Task AnOpenLiveScreenModal_FollowsTheStatus_FallsBackToReading_WhileDisconnected_AndReadsAtOnceOnReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await _host.BrowserAsync("en-US", 1280, 900, mobile: false, ct);
        if (browser is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser print status checks.");
        await LoadAsync(browser, "/orders/live-display", ct);
        Assert.True(await browser.WaitForAsync($"!!document.querySelector('#ordersLiveScreenHost [data-order-detail=\"{Order}\"]')", TimeSpan.FromSeconds(15), ct),
            $"The Live Screen did not render the order. {_host.ServerErrors}");
        await WaitConnectedAsync(browser, ct);
        await browser.EvaluateAsync<bool>($"(document.querySelector('#ordersLiveScreenHost [data-order-detail=\"{Order}\"]').click(), true)", ct);
        Assert.True(await browser.WaitForAsync("!!document.querySelector('#ordersLiveDetailModal [data-print-action-for]')", TimeSpan.FromSeconds(10), ct),
            $"The detail modal did not load its print action. {_host.ServerErrors}");

        // Connected: the modal follows the server through notifications.
        await CreateReceiptAsync(ct);
        await WaitForAsync(browser, "queued", ct);

        // Disconnected: nothing is pushed, and the open modal reads the state again within its fallback interval.
        await browser.EvaluateAsync<bool>("(window.WaslaRealtime.get().stop(), true)", ct);
        Assert.True(await browser.WaitForAsync("window.WaslaRealtime.get().state() === 'disconnected'", TimeSpan.FromSeconds(5), ct));
        var jobId = await JobIdAsync(ct);
        await BridgeAsync(bridge => bridge.TryMarkPrintingAsync(Tenant, jobId, "Kitchen", null, ct));
        Assert.True(await browser.WaitForAsync(ModeIs("printing"), TimeSpan.FromSeconds(15), ct),
            "While disconnected, the open modal did not read the new status within its fallback interval.");

        // Reconnect: read at once (the change below happens while still disconnected, so only that read can show it).
        await BridgeAsync(bridge => bridge.TryMarkFailedAsync(Tenant, jobId, "Printer offline", ct));
        await browser.EvaluateAsync<bool>("(window.WaslaRealtime.get().start(), true)", ct);
        Assert.True(await browser.WaitForAsync(ModeIs("reprint"), Push, ct), "A reconnect did not read the current status at once.");
        var (_, _, _, outcome) = await ReadActionAsync(browser, ct);
        Assert.Equal(Resource("en-US", "Orders.Print.Status.Failed"), outcome);
        Assert.Null(_host.ServerErrors);
    }

    private async Task CreateReceiptAsync(CancellationToken ct)
    {
        using var scope = _host.Services.CreateScope();
        var receipts = scope.ServiceProvider.GetRequiredService<IReceiptPrintJobService>();
        Assert.True(await receipts.TryCreateReceiptJobAsync(Tenant, Order, 1, "Visual Test Restaurant", ct));
    }

    private async Task BridgeAsync(Func<IPrintBridgeJobService, Task<PrintJobClaimResponse>> report)
    {
        using var scope = _host.Services.CreateScope();
        var result = await report(scope.ServiceProvider.GetRequiredService<IPrintBridgeJobService>());
        Assert.Equal(PrintJobClaimResult.Claimed, result.Result);
    }

    private async Task<Guid> JobIdAsync(CancellationToken ct)
    {
        await using var db = await _host.Services.GetRequiredService<ITenantDbContextFactory>().CreateAsync(Tenant, ct);
        return db.PrintJobs.Single(j => j.OrderId == Order && (j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Printing)).Id;
    }

    private static string ModeIs(string mode) =>
        $"(() => {{ const b = document.querySelector('[data-print-action-for] [data-order-print]'); return !!b && b.getAttribute('data-print-mode') === '{mode}'; }})()";

    private async Task WaitForAsync(HeadlessChromium browser, string mode, CancellationToken ct) =>
        Assert.True(await browser.WaitForAsync(ModeIs(mode), Push, ct), $"The print action did not show '{mode}' after the change. {_host.ServerErrors}");

    private async Task WaitConnectedAsync(HeadlessChromium browser, CancellationToken ct) =>
        Assert.True(
            await browser.WaitForAsync("!!window.WaslaRealtime && !!window.WaslaRealtime.get() && window.WaslaRealtime.get().state() === 'connected'", TimeSpan.FromSeconds(15), ct),
            $"The page did not connect to the live hub. {_host.ServerErrors}");

    private static async Task<(string Mode, string Label, bool Disabled, string Outcome)> ReadActionAsync(HeadlessChromium browser, CancellationToken ct)
    {
        var state = await browser.EvaluateAsync<JsonElement>("""
            (() => {
              const root = document.querySelector('[data-print-action-for]');
              const button = root.querySelector('[data-order-print]');
              const outcome = root.querySelector('[data-print-outcome]');
              return {
                mode: button.getAttribute('data-print-mode'),
                label: button.querySelector('[data-print-label]').textContent.trim(),
                disabled: button.disabled,
                outcome: outcome.textContent.trim(),
                role: outcome.getAttribute('role')
              };
            })()
            """, ct);
        Assert.Equal("status", state.GetProperty("role").GetString());
        return (state.GetProperty("mode").GetString()!, state.GetProperty("label").GetString()!, state.GetProperty("disabled").GetBoolean(), state.GetProperty("outcome").GetString()!);
    }

    private async Task LoadAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        // The layout loads Bootstrap from its CDN; one retry absorbs a slow CDN response.
        try
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }
        catch (TimeoutException)
        {
            await browser.NavigateAsync(new Uri(_host.BaseAddress, path), ct);
        }

        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant layout cannot be exercised.");
    }

    private static string Resource(string culture, string key) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name")?.Value == key)
            .Select(element => element.Element("value")?.Value)
            .SingleOrDefault() ?? $"[missing {key} in {culture}]";
}
