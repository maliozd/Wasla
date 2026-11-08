using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// The report filter of <c>GET /api/dashboard/report</c> as it arrives over HTTP (WAS-182): the real Api pipeline, model
/// binding included, with a session from the real Web login. MVC turns a whitespace-only query value into null, which
/// would let <c>platform=%20</c> mean "every platform" or <c>startDate=%20&amp;endDate=%20</c> mean "today"; the action
/// must see the value as submitted, so such values are refused. Omitted and empty parameters keep their documented
/// meaning (docs/orders/reporting.md). The report calculation is replaced by a recorder, so every case also shows
/// whether it ran: an invalid filter answers 400 without it.
/// </summary>
public sealed class OrderReportApiFilterTests(OrderReportApiFilterTests.ApiHosts hosts) : IClassFixture<OrderReportApiFilterTests.ApiHosts>
{
    private const string Route = "/api/dashboard/report";

    /// <summary>Query, then the expected errors as <c>parameter=Code,Code;parameter=Code</c>, by parameter name.</summary>
    public static TheoryData<string, string> Refused => new()
    {
        // Whitespace-only platform: would otherwise be every platform.
        { "?platform=%20", "platform=PlatformInvalid" },
        { "?platform=+", "platform=PlatformInvalid" },
        { "?preset=today&platform=%20%09", "platform=PlatformInvalid" },
        { "?preset=yesterday&platform=%20", "platform=PlatformInvalid" },
        { "?startDate=2026-10-01&endDate=2026-10-05&platform=%20", "platform=PlatformInvalid" },
        // Whitespace-only preset: would otherwise be today, or custom when dates are given.
        { "?preset=%20", "preset=UnknownPreset" },
        { "?preset=%09&platform=Yemeksepeti", "preset=UnknownPreset" },
        { "?preset=%20&startDate=2026-10-01&endDate=2026-10-05", "preset=UnknownPreset" },
        // Whitespace-only dates: would otherwise be missing, so today or an incomplete range.
        { "?startDate=%20&endDate=%20", "endDate=EndDateInvalid;startDate=StartDateInvalid" },
        { "?startDate=%20", "endDate=CustomRangeIncomplete;startDate=StartDateInvalid" },
        { "?preset=yesterday&startDate=%20", "startDate=StartDateInvalid,DatesRequireCustomPreset" },
        { "?preset=last7days&endDate=+", "endDate=EndDateInvalid,DatesRequireCustomPreset" },
        { "?preset=custom&startDate=2026-10-01&endDate=%20", "endDate=EndDateInvalid" },
        { "?preset=custom&startDate=%20&endDate=2026-10-05", "startDate=StartDateInvalid" },
        // Every parameter whitespace-only.
        { "?preset=%20&startDate=%20&endDate=%20&platform=%20",
            "endDate=EndDateInvalid;platform=PlatformInvalid;preset=UnknownPreset;startDate=StartDateInvalid" },
        // Unchanged refusals.
        { "?preset=last90days", "preset=UnknownPreset" },
        { "?platform=Uber", "platform=PlatformInvalid" },
        { "?platform=0", "platform=PlatformInvalid" },
        { "?startDate=2026-10-05&endDate=2026-10-01", "endDate=RangeInverted" },
        { "?startDate=2026-10-01", "endDate=CustomRangeIncomplete" },
    };

    /// <summary>Query, then the expected preset, platform (empty for every platform), day count and custom dates.</summary>
    public static TheoryData<string, OrderReportPreset, string, int, string> Accepted => new()
    {
        // Omitted and empty parameters: today, every platform.
        { "", OrderReportPreset.Today, "", 1, "" },
        { "?preset=", OrderReportPreset.Today, "", 1, "" },
        { "?platform=", OrderReportPreset.Today, "", 1, "" },
        { "?platform", OrderReportPreset.Today, "", 1, "" },
        { "?preset=&startDate=&endDate=&platform=", OrderReportPreset.Today, "", 1, "" },
        { "?preset=last30days&startDate=&endDate=", OrderReportPreset.Last30Days, "", 30, "" },
        { "?startDate=2026-10-01&endDate=2026-10-05&platform=", OrderReportPreset.Custom, "", 5, "2026-10-01..2026-10-05" },
        // Valid filters.
        { "?preset=YESTERDAY&platform=getiryemek", OrderReportPreset.Yesterday, nameof(FoodPlatform.GetirYemek), 1, "" },
        { "?preset=last7days&platform=2", OrderReportPreset.Last7Days, nameof(FoodPlatform.GetirYemek), 7, "" },
        { "?startDate=2026-10-01&endDate=2026-10-05", OrderReportPreset.Custom, "", 5, "2026-10-01..2026-10-05" },
        { "?preset=custom&startDate=2026-10-01&endDate=2026-10-01&platform=TrendyolYemek",
            OrderReportPreset.Custom, nameof(FoodPlatform.TrendyolYemek), 1, "2026-10-01..2026-10-01" },
    };

    /// <summary>Query, then the parameter given twice.</summary>
    public static TheoryData<string, string> Duplicated => new()
    {
        { "?platform=1&platform=2", "platform" },
        { "?platform=&platform=", "platform" },
        { "?platform=%20&platform=1", "platform" },
        { "?preset=today&preset=today", "preset" },
        { "?startDate=2026-10-01&startDate=2026-10-02&endDate=2026-10-05", "startDate" },
        { "?startDate=2026-10-01&endDate=%20&endDate=2026-10-05", "endDate" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task InvalidFilter_Answers400_WithItsCodes_AndRunsNoReport(string query, string expectedErrors)
    {
        hosts.Reports.Calls.Clear();

        using var response = await hosts.Api.GetAsync(Route + query);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{query}: expected 400, got {(int)response.StatusCode} {body}");
        Assert.Equal(expectedErrors, Errors(body));
        Assert.Empty(hosts.Reports.Calls);
    }

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task ValidFilter_RunsTheReportOnce_WithTheDocumentedPeriodAndPlatform(
        string query, OrderReportPreset preset, string platform, int dayCount, string customRange)
    {
        hosts.Reports.Calls.Clear();

        using var response = await hosts.Api.GetAsync(Route + query);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{query}: expected 200, got {(int)response.StatusCode} {body}");
        var call = Assert.Single(hosts.Reports.Calls);
        Assert.Equal(hosts.TenantId, call.TenantId);
        Assert.Equal(preset, call.Filter.Period.Preset);
        Assert.Equal(platform, call.Filter.Platform?.ToString() ?? "");
        Assert.Equal(dayCount, call.Filter.Period.DayCount);
        if (customRange.Length > 0)
            Assert.Equal(customRange, $"{call.Filter.Period.StartDate:yyyy-MM-dd}..{call.Filter.Period.EndDate:yyyy-MM-dd}");

        // The response is the report of that filter, in the existing shape.
        using var json = JsonDocument.Parse(body);
        var period = json.RootElement.GetProperty("period");
        Assert.Equal(OrderReportFilters.PresetValue(preset), period.GetProperty("preset").GetString());
        Assert.Equal(dayCount, period.GetProperty("dayCount").GetInt32());
        Assert.Equal("TRY", json.RootElement.GetProperty("currency").GetString());
        Assert.Equal(dayCount, json.RootElement.GetProperty("days").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(Duplicated))]
    public async Task DuplicateParameter_Answers400_AndRunsNoReport(string query, string parameter)
    {
        hosts.Reports.Calls.Clear();

        using var response = await hosts.Api.GetAsync(Route + query);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{query}: expected 400, got {(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        var codes = json.RootElement.GetProperty("errors").GetProperty(parameter).EnumerateArray().Select(e => e.GetString());
        Assert.Contains(nameof(OrderReportFilterError.DuplicateParameter), codes);
        Assert.Empty(hosts.Reports.Calls);
    }

    /// <summary>The <c>errors</c> of a validation problem as <c>parameter=Code,Code;...</c>, parameters in ordinal order.</summary>
    private static string Errors(string problemJson)
    {
        using var json = JsonDocument.Parse(problemJson);
        return string.Join(";", json.RootElement.GetProperty("errors").EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + string.Join(",", p.Value.EnumerateArray().Select(e => e.GetString()))));
    }

    /// <summary>
    /// Web and Api served in-process on one key ring (as in <c>ApiTenantSessionTests</c>), an Owner signed in through the
    /// Web login form, and the Api's report calculation replaced by <see cref="RecordingReports"/>.
    /// </summary>
    public sealed class ApiHosts : IAsyncLifetime
    {
        private DirectoryInfo _keys = null!;
        private TenantSessionWebHost _web = null!;
        private TenantSessionApiHost _api = null!;
        private TenantSessionClient _webClient = null!;

        public RecordingReports Reports { get; } = new();

        internal TenantSessionClient Api { get; private set; } = null!;

        public Guid TenantId => _web.AlphaId;

        public async ValueTask InitializeAsync()
        {
            _keys = Directory.CreateTempSubdirectory("wasla-report-filter-keys-");
            _web = await TenantSessionWebHost.StartAsync(_keys);
            _api = await TenantSessionApiHost.StartAsync(_web, _keys, configureServices: services =>
            {
                services.RemoveAll<IOrderReportService>();
                services.AddSingleton<IOrderReportService>(Reports);
            });
            var owner = _web.SeedUser(_web.AlphaId, "owner@alpha.test", UserRole.Owner);
            _webClient = _web.Client(TenantSessionWebHost.AlphaHost);
            await _webClient.LoginAsync(owner.Email);
            Api = _webClient.ReplayTo(_api.BaseAddress, _webClient.Host);
        }

        public async ValueTask DisposeAsync()
        {
            Api?.Dispose();
            _webClient?.Dispose();
            await _api.DisposeAsync();
            await _web.DisposeAsync();
            try { _keys.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Records each report request and answers the report of no orders for that filter.</summary>
    public sealed class RecordingReports : IOrderReportService
    {
        public ConcurrentQueue<(Guid TenantId, OrderReportFilter Filter)> Calls { get; } = new();

        public Task<OrderReport> GetAsync(Guid tenantId, OrderReportFilter filter, CancellationToken ct)
        {
            Calls.Enqueue((tenantId, filter));
            return Task.FromResult(OrderReportService.Build(filter, [], DateTime.UtcNow));
        }
    }
}
