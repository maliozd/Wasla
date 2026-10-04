using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Demos;
using Wasla.Application.GuidedSetup;
using Wasla.Application.Orders;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Setup;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Security;
using Wasla.Web.Ui;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.Demos;

/// <summary>
/// The practice order's automatic stages on the Live Screen: the snapshot carries the same deadline the Worker uses,
/// the delivered practice order leaves the Live Screen after its own short stage while training goes on, and real
/// orders keep their own rules. Real services on SQLite with the production model.
/// </summary>
public sealed class GuidedDemoCountdownTests : IDisposable
{
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true, true);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly OperationalModeTestClock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _otherOwner = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WhenTheDeliveredPracticeOrderLeaves_TrainingStaysAtItsLastStep_AndCanStillBeCompleted()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Setup);
        await _tenants.SeedUserAsync(_tenant, _owner);
        await GuidedSetup().StartAsync(_tenant, _owner, new GuidedSetupPosition(GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.Intro), Ct);
        var demos = Demos();
        // As the training panel does: "start practice" records the practice step and opens the practice order.
        Assert.Null((await Coordinator(demos).StartPracticeAsync(_tenant, _owner, Principal(), Ct)).ErrorMessageKey);
        var demo = (await demos.GetActiveAsync(_tenant, _owner, Ct))!;
        foreach (var action in new[] { "approve", "start-preparing", "mark-ready" })
            Assert.True((await demos.ApplyActionAsync(_tenant, _owner, demo.Id, action, Ct)).Succeeded);
        var simulator = new GuidedDemoDeliverySimulator(_tenants, _clock);
        for (var stage = 0; stage < 2; stage++)
        {
            _clock.Now = _clock.Now.Add(GuidedDemoTiming.StageDuration);
            Assert.Equal(1, await simulator.AdvanceDueAsync(_tenant, Ct));
        }

        // The delivered stage ends: the practice order leaves the Live Screen.
        _clock.Now = _clock.Now.Add(GuidedDemoTiming.StageDuration);
        Assert.Null(await demos.GetForLiveScreenAsync(_tenant, _owner, Ct));

        // Nothing about guided setup changed: still in order training, at Step 7, tenant still in Setup.
        Assert.Equal(GuidedSetupStatus.InProgress, (await GuidedSetup().GetAsync(_tenant, _owner, Ct)).Status);
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenant));
        var panel = await Coordinator(demos).GetLiveScreenAsync(_tenant, _owner, Principal(), Ct);
        Assert.Equal(GuidedTrainingSteps.PracticeDelivered, panel!.StepKey);
        Assert.Equal(new GuidedDemoSummary(demo.Id, OrderStatus.Delivered, false), await demos.GetLatestAsync(_tenant, _owner, Ct));

        // "Eğitimi tamamla" still works, from the persisted delivery rather than from what the screen shows.
        var completed = await Coordinator(demos).CompleteTrainingAsync(_tenant, _owner, Principal(), Ct);
        Assert.Null(completed.ErrorMessageKey);
        Assert.Equal(GuidedSetupCoordinator.TrainingCompletedMessageKey, completed.SuccessMessageKey);
        Assert.Equal(GuidedSetupStatus.Completed, (await GuidedSetup().GetAsync(_tenant, _owner, Ct)).Status);
    }

    [Fact]
    public async Task RealDeliveredOrders_KeepTheirTwoMinuteWindow_LongAfterAPracticeOrderStage()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        var now = _clock.UtcNow;
        var stillShown = await SeedOrderAsync("delivered-30s", OrderStatus.Delivered, now.AddMinutes(-10), now.AddSeconds(-30));
        var almostTwoMinutes = await SeedOrderAsync("delivered-119s", OrderStatus.Delivered, now.AddMinutes(-10), now.AddSeconds(-119));
        var gone = await SeedOrderAsync("delivered-121s", OrderStatus.Delivered, now.AddMinutes(-10), now.AddSeconds(-121));

        var snapshot = await new OrderReadService(_tenants, _clock, NullLogger<OrderReadService>.Instance).GetLiveScreenSnapshotAsync(_tenant, Ct);

        Assert.Contains(snapshot.Orders, o => o.Id == stillShown);
        Assert.Contains(snapshot.Orders, o => o.Id == almostTwoMinutes);
        Assert.DoesNotContain(snapshot.Orders, o => o.Id == gone);
        // Real orders never carry a practice-order countdown.
        Assert.All(snapshot.Orders, o => Assert.Null(o.DemoAutomation));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, Web));
        Assert.All(json.RootElement.GetProperty("orders").EnumerateArray(), o => Assert.Equal(JsonValueKind.Null, o.GetProperty("demoAutomation").ValueKind));
    }

    [Fact]
    public async Task OnlyTheOwnersOwnPracticeOrder_CarriesTheDeadline_InTheSnapshotContract()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Setup);
        await _tenants.SeedUserAsync(_tenant, _owner);
        await _tenants.SeedUserAsync(_tenant, _otherOwner);
        var demos = Demos();
        var demo = await demos.StartAsync(_tenant, _owner, Ct);
        foreach (var action in new[] { "approve", "start-preparing" })
            Assert.True((await demos.ApplyActionAsync(_tenant, _owner, demo.Id, action, Ct)).Succeeded);

        // The restaurant moves it itself until Mark ready: no countdown.
        var preparing = GuidedDemoLiveMapper.ToLiveOrder(new KeyLocalizer(), (await demos.GetForLiveScreenAsync(_tenant, _owner, Ct))!);
        Assert.Null(preparing.DemoAutomation);

        Assert.True((await demos.ApplyActionAsync(_tenant, _owner, demo.Id, "mark-ready", Ct)).Succeeded);
        var readyAt = _clock.UtcNow;
        _clock.Now = _clock.Now.AddSeconds(5);
        var ready = GuidedDemoLiveMapper.ToLiveOrder(new KeyLocalizer(), (await demos.GetForLiveScreenAsync(_tenant, _owner, Ct))!);

        Assert.True(ready.IsDemo);
        Assert.Equal(new LiveScreenDemoAutomation(GuidedDemoTiming.PickUp, readyAt.Add(GuidedDemoTiming.StageDuration), 20), ready.DemoAutomation);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ready, Web));
        var automation = json.RootElement.GetProperty("demoAutomation");
        Assert.Equal(["action", "dueAtUtc", "durationSeconds"], automation.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("PickUp", automation.GetProperty("action").GetString());
        Assert.EndsWith("Z", automation.GetProperty("dueAtUtc").GetString(), StringComparison.Ordinal);
        Assert.Equal(20, automation.GetProperty("durationSeconds").GetInt32());

        // Another user, and another tenant, never see it.
        Assert.Null(await demos.GetForLiveScreenAsync(_tenant, _otherOwner, Ct));
        Assert.Null(await demos.GetForLiveScreenAsync(Guid.NewGuid(), _owner, Ct));
    }

    public void Dispose() => _tenants.Dispose();

    private GuidedSetupService GuidedSetup() => new(_tenants, _clock);

    private GuidedDemoService Demos() => new(_tenants, new NoSubtypes(), _clock);

    private GuidedSetupCoordinator Coordinator(GuidedDemoService demos) =>
        new(GuidedSetup(), new FixedNavigation(Owner), new FakeSetupStatus(), demos, new FakePrintBridgeDevices(), new CapturingLogger());

    private ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _owner.ToString())], "Tenant"));

    private async Task<Guid> SeedOrderAsync(string code, OrderStatus status, DateTime receivedAt, DateTime? deliveredAt)
    {
        var id = Guid.NewGuid();
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = code,
            ExternalOrderCode = code,
            IdempotencyKey = code,
            InternalStatus = status,
            PlatformStatus = "Delivered",
            CustomerName = "Test Customer",
            CustomerPhone = "+905550000000",
            CustomerAddress = "Test Address",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = receivedAt,
            ReceivedAt = receivedAt,
            DeliveredAt = deliveredAt,
            RawPayloadJson = "{}",
            CreatedAt = receivedAt,
            UpdatedAt = deliveredAt ?? receivedAt,
            Items = [new OrderItem { ProductName = "Lahmacun", Quantity = 1, UnitPrice = 100m, TotalPrice = 100m, CreatedAt = receivedAt, UpdatedAt = receivedAt }]
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private sealed class NoSubtypes : ITenantBusinessSubtypeReader
    {
        public Task<IReadOnlyList<string>?> GetSubtypeCodesAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(null);
    }

    private sealed class KeyLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}

/// <summary>The countdown copy: every culture, one placeholder for the seconds, no number written into a sentence.</summary>
public sealed class GuidedDemoCountdownCopyTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];
    private static readonly string[] WithSeconds = ["PickUp", "Deliver", "Leave", "Seconds"];
    private static readonly string[] WithoutSeconds = ["Waiting", "Label", "Hint"];

    [Fact]
    public void CountdownCopy_IsTheAgreedTurkish_AndEveryCultureCarriesTheSecondsAsAPlaceholder()
    {
        foreach (var turkish in new[] { "", ".tr-TR" })
        {
            var values = Load(turkish);
            Assert.Equal("Kurye siparişi {0} saniye içinde teslim alacak.", values["Orders.DemoCountdown.PickUp"]);
            Assert.Equal("Teslimat {0} saniye içinde tamamlanacak.", values["Orders.DemoCountdown.Deliver"]);
            Assert.Equal("Deneme siparişi {0} saniye içinde Canlı Ekran'dan kalkacak.", values["Orders.DemoCountdown.Leave"]);
            Assert.Equal("Platformdan güncelleme bekleniyor…", values["Orders.DemoCountdown.Waiting"]);
        }

        var manager = new System.Resources.ResourceManager("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);
        foreach (var culture in Cultures)
        {
            var values = Load(culture);
            var info = culture.Length == 0 ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo(culture[1..]);
            foreach (var key in WithSeconds.Concat(WithoutSeconds).Select(k => "Orders.DemoCountdown." + k))
            {
                Assert.False(string.IsNullOrWhiteSpace(values.GetValueOrDefault(key)), $"{key} in SharedResource{culture}.resx");
                Assert.DoesNotMatch(@"\d{2}", values[key].Replace("{0}", string.Empty, StringComparison.Ordinal));
                Assert.Equal(values[key], manager.GetString(key, info));
            }
            foreach (var key in WithSeconds)
                Assert.Equal(["{0}"], System.Text.RegularExpressions.Regex.Matches(values["Orders.DemoCountdown." + key], @"\{\d+\}").Select(m => m.Value));
            foreach (var key in WithoutSeconds)
                Assert.DoesNotMatch(@"\{\d+\}", values["Orders.DemoCountdown." + key]);
            if (culture is ".en-US" or ".ar-SA" or ".ru-RU")
                Assert.NotEqual(Load(".tr-TR")["Orders.DemoCountdown.PickUp"], values["Orders.DemoCountdown.PickUp"]);
        }
        Assert.Matches(@"\p{IsArabic}", Load(".ar-SA")["Orders.DemoCountdown.Leave"]);
    }

    private static Dictionary<string, string> Load(string culture) =>
        System.Xml.Linq.XDocument.Load(Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
