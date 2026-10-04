using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Printing;
using Wasla.UnitTests.DevelopmentTools;
using Wasla.Web;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.PrintBridge;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// The Print Bridge Devices page's device snapshot: the page's initial state, the periodic <c>GET devices/list</c> and the
/// device toggle's response share one contract. It reads only the current tenant's devices, carries every time as explicit
/// UTC, and takes its presence thresholds from <see cref="PrintBridgeConnectionStatusCalculator"/>.
/// </summary>
public sealed class PrintBridgeDevicesSnapshotTests
{
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true, true);
    private static readonly DateTime Now = new(2026, 10, 1, 8, 49, 30, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly ClaimsPrincipal _principal =
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Tenant"));
    private readonly TogglingDevices _devices = new();

    [Fact]
    public void The_device_list_is_an_authorized_uncached_read()
    {
        var method = typeof(PrintBridgeController).GetMethod(nameof(PrintBridgeController.ListDevices))!;

        Assert.Equal("devices/list", Assert.Single(method.GetCustomAttributes<HttpGetAttribute>()).Template);
        Assert.Empty(method.GetCustomAttributes<HttpPostAttribute>());
        Assert.Equal(TenantPolicies.CanManagePrintBridgeDevices, Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Equal(AuthSchemes.Tenant,
            Assert.Single(typeof(PrintBridgeController).GetCustomAttributes<AuthorizeAttribute>()).AuthenticationSchemes);

        var cache = Assert.Single(method.GetCustomAttributes<ResponseCacheAttribute>());
        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);
    }

    [Fact]
    public async Task The_device_list_contains_only_the_current_tenants_devices()
    {
        var own = _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: DateTime.UtcNow);
        _devices.Add(_otherTenant, "Other restaurant", lastSeenAtUtc: DateTime.UtcNow);

        var snapshot = await ListAsync();

        Assert.Equal(own.Id, Assert.Single(snapshot.Devices).Id);
        Assert.DoesNotContain(snapshot.Devices, d => d.Name == "Other restaurant");
        Assert.Equal(1, snapshot.Quota.ConnectedDeviceCount);
        Assert.Equal("Kitchen", snapshot.Quota.LastConnectedDeviceName);
        Assert.All(_devices.ListedTenants, tenant => Assert.Equal(_tenant, tenant));
        Assert.All(_devices.QuotaTenants, tenant => Assert.Equal(_tenant, tenant));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void Every_time_is_explicit_utc_whatever_kind_the_database_returns(DateTimeKind kind)
    {
        var seen = new DateTime(2026, 10, 1, 8, 49, 0, DateTimeKind.Utc);
        var stored = kind switch
        {
            DateTimeKind.Local => seen.ToLocalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(seen, DateTimeKind.Unspecified),
            _ => seen
        };
        var device = Summary("Kitchen", lastSeenAtUtc: stored);

        var serverNow = kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(Now, DateTimeKind.Unspecified) : Now;

        using var json = Json(Create([device, Summary("New", lastSeenAtUtc: null)], serverNow));
        var root = json.RootElement;
        var row = root.GetProperty("devices")[0];

        Assert.Equal("2026-10-01T08:49:00Z", row.GetProperty("lastSeenAtUtc").GetString());
        Assert.Equal("2026-10-01T08:49:00Z", root.GetProperty("quota").GetProperty("latestLastSeenAtUtc").GetString());
        Assert.Equal("2026-10-01T08:49:30Z", root.GetProperty("serverTimeUtc").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("devices")[1].GetProperty("lastSeenAtUtc").ValueKind);
    }

    [Fact]
    public void The_presence_thresholds_are_the_calculators_own()
    {
        using var json = Json(Create([Summary("Kitchen", lastSeenAtUtc: Now)]));

        Assert.Equal((int)PrintBridgeConnectionStatusCalculator.ConnectedThreshold.TotalSeconds,
            json.RootElement.GetProperty("connectedThresholdSeconds").GetInt32());
        Assert.Equal((int)PrintBridgeConnectionStatusCalculator.RecentlySeenThreshold.TotalSeconds,
            json.RootElement.GetProperty("recentlySeenThresholdSeconds").GetInt32());
        Assert.Equal(60, json.RootElement.GetProperty("connectedThresholdSeconds").GetInt32());
    }

    [Fact]
    public void The_summary_values_come_from_the_same_device_list()
    {
        var snapshot = Create(
        [
            Summary("Kitchen", lastSeenAtUtc: Now.AddSeconds(-10)),
            Summary("Bar", lastSeenAtUtc: Now.AddMinutes(-2)),
            Summary("Old", isActive: false, lastSeenAtUtc: Now.AddSeconds(-5)),
            Summary("New", lastSeenAtUtc: null)
        ]);

        Assert.Equal(new[] { "Connected", "RecentlySeen", "Inactive", "NeverConnected" }, snapshot.Devices.Select(d => d.ConnectionStatus));
        Assert.Equal(new[] { true, false, false, false }, snapshot.Devices.Select(d => d.IsConnected));
        Assert.Equal(1, snapshot.Quota.ConnectedDeviceCount);
        Assert.Equal(Now.AddSeconds(-5), snapshot.Quota.LatestLastSeenAtUtc);
        Assert.Equal("Old", snapshot.Quota.LastConnectedDeviceName);
        Assert.Equal(3, snapshot.Quota.ActiveDeviceCount);
        Assert.Equal(PrintBridgeDeviceLimits.AllowedActiveDeviceCount, snapshot.Quota.AllowedActiveDeviceCount);
        Assert.All(snapshot.Devices, d => Assert.Equal($"/print-bridge/devices/{d.Id}", d.DetailsUrl));

        var empty = Create([]);
        Assert.Empty(empty.Devices);
        Assert.Equal(0, empty.Quota.ConnectedDeviceCount);
        Assert.Null(empty.Quota.LatestLastSeenAtUtc);
        Assert.Null(empty.Quota.LastConnectedDeviceName);
    }

    [Fact]
    public async Task The_page_initial_state_and_the_refresh_share_one_contract()
    {
        _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: DateTime.UtcNow);

        var view = Assert.IsType<ViewResult>(await Controller().Devices(CancellationToken.None));
        var initial = Assert.IsType<PrintBridgePageViewModel>(view.Model).DeviceSnapshot;
        Assert.NotNull(initial);
        var refreshed = await ListAsync();

        using var initialJson = Json(initial);
        using var refreshedJson = Json(refreshed);
        Assert.Equal(Keys(initialJson.RootElement), Keys(refreshedJson.RootElement));
        Assert.Equal(Keys(initialJson.RootElement.GetProperty("devices")[0]), Keys(refreshedJson.RootElement.GetProperty("devices")[0]));
        Assert.Equal(Keys(initialJson.RootElement.GetProperty("quota")), Keys(refreshedJson.RootElement.GetProperty("quota")));
        Assert.Equal(initial.Devices, refreshed.Devices);
        Assert.Equal(initial.Quota, refreshed.Quota);
    }

    [Fact]
    public async Task A_device_toggle_answers_with_the_same_snapshot()
    {
        var device = _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: DateTime.UtcNow);

        var ok = Assert.IsType<OkObjectResult>(await Controller().SetDeviceActive(device.Id, false, CancellationToken.None));
        using var json = Json(ok.Value);
        using var snapshot = Json(await ListAsync());
        var root = json.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("isActive").GetBoolean());
        Assert.EndsWith("Z", root.GetProperty("serverTimeUtc").GetString());
        Assert.EndsWith("Z", root.GetProperty("devices")[0].GetProperty("lastSeenAtUtc").GetString());
        Assert.Equal("Inactive", root.GetProperty("devices")[0].GetProperty("connectionStatus").GetString());
        Assert.Equal(60, root.GetProperty("connectedThresholdSeconds").GetInt32());
        Assert.Subset(Keys(root).ToHashSet(), Keys(snapshot.RootElement).ToHashSet());
        Assert.Equal(Keys(snapshot.RootElement.GetProperty("devices")[0]), Keys(root.GetProperty("devices")[0]));
        Assert.Equal(Keys(snapshot.RootElement.GetProperty("quota")), Keys(root.GetProperty("quota")));
    }

    [Fact]
    public void A_device_name_cannot_break_out_of_the_inline_initial_state()
    {
        var json = JsonSerializer.Serialize(
            Create([Summary("</script><script>alert(1)</script>", lastSeenAtUtc: Now)]), PrintBridgeDeviceSnapshot.JsonOptions);

        Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_page_renders_times_in_the_restaurant_zone_and_ui_culture_from_one_refresh_path()
    {
        var view = File.ReadAllText(Path.Combine(Root(), "src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Devices.cshtml"));
        var script = File.ReadAllText(Path.Combine(Root(), "src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-page.js"));
        var snapshot = File.ReadAllText(Path.Combine(Root(), "src", "Wasla.Web", "Models", "PrintBridge", "PrintBridgeDeviceSnapshot.cs"));

        Assert.Contains("JsonSerializer.Serialize(Model.DeviceSnapshot, Wasla.Web.Models.PrintBridge.PrintBridgeDeviceSnapshot.JsonOptions)", view);
        Assert.Contains("initialState: @Html.Raw(initialSnapshotJson)", view);
        Assert.Contains("displayCulture: \"@System.Globalization.CultureInfo.CurrentUICulture.Name\"", view);
        Assert.Contains("TimeZoneHelper.ResolveTurkeyTimeZone()", view);
        Assert.Contains("timeZoneId: \"@timeZoneId\"", view);
        Assert.Contains("devicesPollIntervalMs: 15000", view);
        Assert.Contains("devicesUrl: \"/print-bridge/devices/list\"", view);

        Assert.Contains("new Intl.DateTimeFormat(displayCulture(), options)", script);
        Assert.Contains("cfg.refreshDevices = requestDevicesRefresh;", script);
        Assert.Contains("data.connectedThresholdSeconds", script);
        Assert.Contains("data.serverTimeUtc", script);
        foreach (var source in new[] { view, script, snapshot })
        {
            Assert.DoesNotContain("ToLocalTime", source);
            Assert.DoesNotContain("toLocaleString(", source);
            Assert.DoesNotContain("toLocaleDateString(", source);
            Assert.DoesNotContain("toLocaleTimeString(", source);
        }
        foreach (var pushTransport in new[] { "HubConnection", "signalR", "EventSource", "WebSocket" })
            Assert.DoesNotContain(pushTransport, script);
    }

    private async Task<PrintBridgeDeviceSnapshot> ListAsync()
    {
        var ok = Assert.IsType<OkObjectResult>(await Controller().ListDevices(CancellationToken.None));
        return Assert.IsType<PrintBridgeDeviceSnapshot>(ok.Value);
    }

    private PrintBridgeController Controller()
    {
        var httpContext = new DefaultHttpContext { User = _principal };
        return new PrintBridgeController(
            new FixedTenant(_tenant),
            _devices,
            null!,
            new NoPrintJobs(),
            new TestHostEnvironment("Development"),
            new ConfigurationBuilder().Build(),
            new KeyLocalizer(),
            new GuidedSetupCoordinator(new FakeGuidedSetup(), new FixedNavigation(Owner), new FakeSetupStatus(), new FakeDemos(), _devices,
                new CapturingLogger()),
            null!)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider()),
            Url = new NoRouteUrlHelper()
        };
    }

    private static PrintBridgeDeviceSnapshot Create(IReadOnlyList<PrintBridgeDeviceSummaryDto> devices, DateTime? utcNow = null) =>
        PrintBridgeDeviceSnapshot.Create(
            devices,
            new PrintBridgeDeviceQuotaDto(PrintBridgeDeviceLimits.AllowedActiveDeviceCount, devices.Count(d => d.IsActive), true, false),
            utcNow ?? Now,
            id => $"/print-bridge/devices/{id}");

    private static PrintBridgeDeviceSummaryDto Summary(string name, bool isActive = true, DateTime? lastSeenAtUtc = null) =>
        new(Guid.NewGuid(), name, isActive, lastSeenAtUtc, "KITCHEN-PC", null, "POS-58", "1.4.0",
            PrintBridgeConnectionStatusCalculator.Calculate(isActive, lastSeenAtUtc, Now));

    /// <summary>As MVC writes it (no custom JSON options are registered): the web defaults.</summary>
    private static JsonDocument Json(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static string[] Keys(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Wasla.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Tenant-scoped devices whose active flag can be toggled; it records which tenant every read used.</summary>
    private sealed class TogglingDevices : IPrintBridgeDeviceManagementService
    {
        private readonly Dictionary<Guid, List<PrintBridgeDeviceSummaryDto>> _byTenant = new();

        public List<Guid> ListedTenants { get; } = new();
        public List<Guid> QuotaTenants { get; } = new();

        public PrintBridgeDeviceSummaryDto Add(Guid tenantId, string name, bool isActive = true, DateTime? lastSeenAtUtc = null)
        {
            // As read back from the database: a UTC value without a kind.
            var stored = lastSeenAtUtc is { } seen ? DateTime.SpecifyKind(seen, DateTimeKind.Unspecified) : (DateTime?)null;
            var device = new PrintBridgeDeviceSummaryDto(Guid.NewGuid(), name, isActive, stored, "PC", null, null, null,
                PrintBridgeConnectionStatusCalculator.Calculate(isActive, lastSeenAtUtc));
            if (!_byTenant.TryGetValue(tenantId, out var devices))
                _byTenant[tenantId] = devices = new List<PrintBridgeDeviceSummaryDto>();
            devices.Add(device);
            return device;
        }

        public Task<IReadOnlyList<PrintBridgeDeviceSummaryDto>> ListDevicesAsync(Guid customerId, CancellationToken ct)
        {
            ListedTenants.Add(customerId);
            return Task.FromResult<IReadOnlyList<PrintBridgeDeviceSummaryDto>>(
                _byTenant.TryGetValue(customerId, out var devices) ? devices.ToArray() : []);
        }

        public Task<PrintBridgeDeviceQuotaDto> GetDeviceQuotaAsync(Guid customerId, CancellationToken ct)
        {
            QuotaTenants.Add(customerId);
            var active = _byTenant.TryGetValue(customerId, out var devices) ? devices.Count(d => d.IsActive) : 0;
            return Task.FromResult(new PrintBridgeDeviceQuotaDto(PrintBridgeDeviceLimits.AllowedActiveDeviceCount, active,
                active < PrintBridgeDeviceLimits.AllowedActiveDeviceCount, active > PrintBridgeDeviceLimits.AllowedActiveDeviceCount));
        }

        public Task<bool> SetDeviceActiveAsync(Guid customerId, Guid deviceId, bool isActive, CancellationToken ct)
        {
            if (!_byTenant.TryGetValue(customerId, out var devices)) return Task.FromResult(false);
            var index = devices.FindIndex(d => d.Id == deviceId);
            if (index < 0) return Task.FromResult(false);
            var device = devices[index];
            devices[index] = device with
            {
                IsActive = isActive,
                ConnectionStatus = PrintBridgeConnectionStatusCalculator.Calculate(isActive,
                    device.LastSeenAtUtc is { } seen ? DateTime.SpecifyKind(seen, DateTimeKind.Utc) : null)
            };
            return Task.FromResult(true);
        }

        public Task<PrintBridgeDeviceDetailsDto?> GetDeviceDetailsAsync(Guid customerId, Guid deviceId, CancellationToken ct) =>
            Task.FromResult<PrintBridgeDeviceDetailsDto?>(null);

        public Task<GeneratePrintBridgeTokenResult> CreateDeviceAsync(Guid customerId, string deviceName, CancellationToken ct) => throw Forbidden();
        public Task<GeneratePrintBridgeTokenResult> RegenerateTokenAsync(Guid customerId, Guid deviceId, CancellationToken ct) => throw Forbidden();
        public Task<RemovePrintBridgeDeviceResult> RemoveDeviceAsync(Guid customerId, Guid deviceId, CancellationToken ct) => throw Forbidden();
        public Task<RenamePrintBridgeDeviceResult> UpdateDeviceNameAsync(Guid customerId, Guid deviceId, string deviceName, CancellationToken ct) => throw Forbidden();

        private static Exception Forbidden([System.Runtime.CompilerServices.CallerMemberName] string? action = null) =>
            new InvalidOperationException($"The Devices page refresh must not call {action}.");
    }

    private sealed class NoPrintJobs : IPrintJobHistoryService
    {
        public Task<IReadOnlyList<PrintJobHistoryItemDto>> GetRecentReceiptJobsAsync(Guid customerId, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrintJobHistoryItemDto>>([]);

        public Task<ReprintReceiptResult> CreateReprintAsync(Guid customerId, Guid printJobId, string? tenantDisplayName, CancellationToken ct) =>
            throw new InvalidOperationException("The Devices page refresh must not reprint.");
    }

    private sealed class NoRouteUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private sealed class KeyLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
