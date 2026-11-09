using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Orders;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;
using Wasla.Web.Models.Reports;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// WAS-200: how the report classifies a cancelled order and what the page's reason rows are made of, from known groups.
/// The aggregate statement itself is covered on SQL Server by <c>OrderReportSqlServerTests</c>.
/// </summary>
public sealed class OrderReportCancellationBreakdownTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 11, 7);

    private static OrderReportFilter Filter() =>
        OrderReportFilters.Resolve("yesterday", null, null, null, Now, TimeZoneHelper.ResolveTurkeyTimeZone()).Filter!;

    private static OrderReportService.Group Cancelled(FoodPlatform platform, int count, OrderCancellationOrigin? origin, int? code = null) =>
        new(platform, OrderStatus.Cancelled, Day, count, count * 10m, origin, code is null ? null : platform, code);

    [Theory]
    [InlineData(null, OrderReportCancellationOutcome.Unknown)]
    [InlineData(OrderCancellationOrigin.OperatorRejected, OrderReportCancellationOutcome.KnownRejected)]
    [InlineData(OrderCancellationOrigin.OperatorCancelled, OrderReportCancellationOutcome.KnownCancelled)]
    [InlineData(OrderCancellationOrigin.ProviderOrCustomer, OrderReportCancellationOutcome.KnownCancelled)]
    [InlineData(OrderCancellationOrigin.RestaurantOnPlatform, OrderReportCancellationOutcome.Unknown)]
    [InlineData((OrderCancellationOrigin)99, OrderReportCancellationOutcome.Unknown)]
    public void Origin_DecidesWhatTheReportCanSay(OrderCancellationOrigin? origin, OrderReportCancellationOutcome expected) =>
        Assert.Equal(expected, OrderReportStatuses.ClassifyCancellation(origin));

    [Fact]
    public void EveryDefinedOrigin_HasAClassification()
    {
        // A new origin must be decided here, not fall silently into unknown.
        Assert.Equal(
            [OrderCancellationOrigin.OperatorRejected, OrderCancellationOrigin.OperatorCancelled, OrderCancellationOrigin.RestaurantOnPlatform, OrderCancellationOrigin.ProviderOrCustomer],
            Enum.GetValues<OrderCancellationOrigin>());
    }

    [Fact]
    public void RestaurantOrigin_IsKnownOrigin_ButUnknownOutcome()
    {
        var figures = OrderReportFigures.ForStatus(OrderStatus.Cancelled, 3, 30m, OrderCancellationOrigin.RestaurantOnPlatform, hasReason: true);

        Assert.Equal(0, figures.KnownRejectedOrderCount);
        Assert.Equal(0, figures.KnownCancelledOrderCount);
        Assert.Equal(3, figures.UnknownOutcomeOrderCount);
        Assert.Equal(3, figures.RestaurantOriginUnknownOutcomeOrderCount);
        Assert.Equal(0, figures.CancellationReasonMissingOrderCount);
    }

    [Fact]
    public void AnOriginOrReason_OnAnOrderThatIsNotCancelled_CountsForNothing()
    {
        var report = OrderReportService.Build(Filter(), [new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, Day, 2, 20m, OrderCancellationOrigin.OperatorRejected, FoodPlatform.TrendyolYemek, 621)], Now);

        Assert.Equal(0, report.Summary.KnownRejectedOrderCount);
        Assert.Equal(0, report.Summary.CancellationReasonMissingOrderCount);
        Assert.Empty(report.CancellationReasons);
    }

    [Fact]
    public void ReasonRows_AddUpAcrossOrigins_AndAreOrderedByCountThenPlatformThenCode()
    {
        var report = OrderReportService.Build(Filter(),
        [
            Cancelled(FoodPlatform.TrendyolYemek, 1, OrderCancellationOrigin.OperatorRejected, 622),
            Cancelled(FoodPlatform.TrendyolYemek, 2, OrderCancellationOrigin.RestaurantOnPlatform, 622),
            Cancelled(FoodPlatform.TrendyolYemek, 1, OrderCancellationOrigin.ProviderOrCustomer, 601),
            Cancelled(FoodPlatform.Yemeksepeti, 1, OrderCancellationOrigin.OperatorRejected, 601),
            Cancelled(FoodPlatform.TrendyolYemek, 4, null)
        ], Now);

        Assert.Equal(
            [
                new OrderReportCancellationReasonRow(FoodPlatform.TrendyolYemek, 622, 3),
                new OrderReportCancellationReasonRow(FoodPlatform.Yemeksepeti, 601, 1),
                new OrderReportCancellationReasonRow(FoodPlatform.TrendyolYemek, 601, 1)
            ],
            report.CancellationReasons);
        Assert.Equal(4, report.Summary.CancellationReasonMissingOrderCount);
        Assert.Equal(9, report.CancellationReasons.Sum(r => r.OrderCount) + report.Summary.CancellationReasonMissingOrderCount);
    }

    [Fact]
    public void ReasonGroups_KeepPlatformsApart_AndDivideByEachPlatformsCancelledOrders()
    {
        using var culture = new CultureScope("en-US");
        var report = OrderReportService.Build(Filter(),
        [
            Cancelled(FoodPlatform.TrendyolYemek, 3, OrderCancellationOrigin.OperatorRejected, 621),
            Cancelled(FoodPlatform.TrendyolYemek, 1, OrderCancellationOrigin.ProviderOrCustomer, 604),
            Cancelled(FoodPlatform.TrendyolYemek, 4, null),
            Cancelled(FoodPlatform.Yemeksepeti, 2, OrderCancellationOrigin.OperatorRejected)
        ], Now);

        var groups = new ReportPresenter(new KeyLocalizer()).ReasonGroups(report);

        Assert.Equal([FoodPlatform.TrendyolYemek, FoodPlatform.Yemeksepeti], groups.Select(g => g.Platform));
        var trendyol = groups[0];
        Assert.Equal((8, 4, 4), (trendyol.CancelledCount, trendyol.RecordedCount, trendyol.MissingCount));
        Assert.Equal(0.5m, trendyol.Coverage);
        Assert.Equal(0.5m, trendyol.MissingShare);
        Assert.Equal([0.375m, 0.125m], trendyol.Reasons.Select(r => r.Share!.Value));
        Assert.Equal(1m, trendyol.Reasons.Sum(r => r.Share!.Value) + trendyol.MissingShare!.Value);
        Assert.Equal(100d, trendyol.MissingBarPercent);
        Assert.Equal(75d, trendyol.Reasons[0].BarPercent);
        Assert.True(trendyol.HasReasonList);

        var yemeksepeti = groups[1];
        Assert.Equal((2, 0, 2), (yemeksepeti.CancelledCount, yemeksepeti.RecordedCount, yemeksepeti.MissingCount));
        Assert.Empty(yemeksepeti.Reasons);
        Assert.Equal(1m, yemeksepeti.MissingShare);
        Assert.False(yemeksepeti.HasReasonList);
    }

    [Fact]
    public void ReasonGroups_LeaveOutPlatformsWithoutCancellations()
    {
        var report = OrderReportService.Build(Filter(), [new(FoodPlatform.GetirYemek, OrderStatus.Delivered, Day, 2, 20m)], Now);

        Assert.Empty(new ReportPresenter(new KeyLocalizer()).ReasonGroups(report));
    }

    [Fact]
    public void EndingRows_ShareTheCancelledOrders_AndHaveNoShareWithoutThem()
    {
        var mixed = OrderReportFigures.ForStatus(OrderStatus.Cancelled, 1, 0m, OrderCancellationOrigin.OperatorRejected)
                    + OrderReportFigures.ForStatus(OrderStatus.Cancelled, 1, 0m, OrderCancellationOrigin.ProviderOrCustomer)
                    + OrderReportFigures.ForStatus(OrderStatus.Cancelled, 2, 0m);

        Assert.Equal([0.25m, 0.25m, 0.5m], ReportPresenter.EndingRows(mixed).Select(r => r.Share!.Value));
        Assert.All(ReportPresenter.EndingRows(OrderReportFigures.Empty), r => Assert.Null(r.Share));
    }

    [Fact]
    public void AStoredCodeWithoutALabel_IsShownByItsNumber_NotDropped()
    {
        using var culture = new CultureScope("en-US");
        var report = OrderReportService.Build(Filter(), [Cancelled(FoodPlatform.TrendyolYemek, 1, OrderCancellationOrigin.ProviderOrCustomer, 9001)], Now);

        var reason = Assert.Single(new ReportPresenter(new MissingKeysLocalizer()).ReasonGroups(report)[0].Reasons);

        Assert.Equal("Reason code 9001", reason.Label);
        Assert.Equal(9001, reason.Code);
    }

    private static Dictionary<string, string> Resources(string culture) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData(".tr-TR")]
    [InlineData(".en-US")]
    [InlineData(".ar-SA")]
    [InlineData(".ru-RU")]
    public void EveryDocumentedCode_HasALabel_InEveryLanguage(string culture)
    {
        var resources = Resources(culture);
        var codes = ProviderCancellationReasons.For(FoodPlatform.TrendyolYemek);

        Assert.Equal(31, codes.Count);
        foreach (var code in codes)
        {
            var key = ProviderCancellationReasons.LabelKey(FoodPlatform.TrendyolYemek, code);
            Assert.True(resources.TryGetValue(key, out var label) && !string.IsNullOrWhiteSpace(label), $"{key} is missing in SharedResource{culture}.resx");
        }
    }

    [Fact]
    public void ACodeAnOperatorCanChoose_KeepsItsExistingLabel_AndNoProviderCodeBecomesSelectable()
    {
        Assert.Equal("Orders.RejectReason.TrendyolYemek.621", ProviderCancellationReasons.LabelKey(FoodPlatform.TrendyolYemek, 621));
        Assert.Equal("Reports.CancellationReason.TrendyolYemek.604", ProviderCancellationReasons.LabelKey(FoodPlatform.TrendyolYemek, 604));
        Assert.Equal([621, 622, 623, 627], OrderRejectReasons.For(FoodPlatform.TrendyolYemek));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _culture;
    }

    private sealed class KeyLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    /// <summary>Only the fallback wording resolves, as for a stored code no resource exists for.</summary>
    private sealed class MissingKeysLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => name == "Reports.CancellationReason.Unlabelled"
            ? new(name, "Reason code {0}")
            : new(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
