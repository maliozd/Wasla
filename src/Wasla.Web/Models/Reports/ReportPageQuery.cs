using Microsoft.AspNetCore.Http;
using Wasla.Application.Reports;

namespace Wasla.Web.Models.Reports;

/// <summary>The sections the report page ships. Other design sections stay out of navigation until their data exists.</summary>
public static class ReportSections
{
    public const string Overview = "overview";
    public const string Platforms = "platforms";
    public const string Cancellations = "cancellations";

    public static readonly IReadOnlyList<string> All = [Overview, Platforms, Cancellations];
}

/// <summary>
/// The report page's state as it stands in the query string, with the parameter names of <c>GET /api/dashboard/report</c>
/// plus <c>compare</c> and <c>section</c>. Values are kept as submitted: <see cref="OrderReportFilters"/> decides whether
/// they are valid. <c>reports-page.js</c> builds the same URLs (<c>toQuery</c>), so reload, back and tab changes keep them.
/// </summary>
public sealed record ReportPageQuery(
    string Preset,
    string? StartDate,
    string? EndDate,
    string? Platform,
    bool Compare,
    string Section,
    IReadOnlyList<string> DuplicatedParameters)
{
    /// <summary>The page's own default period; the Dashboard already covers today.</summary>
    public const string DefaultPreset = "last30days";

    public const string CompareParameter = "compare";
    public const string SectionParameter = "section";

    public const string PagePath = "/reports";

    public bool IsCustom => string.Equals(Preset, OrderReportFilters.PresetValue(OrderReportPreset.Custom), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when something differs from the page default, so the reset action applies.</summary>
    public bool HasNonDefaultFilter =>
        !string.Equals(Preset, DefaultPreset, StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Platform)
        || Compare;

    public static ReportPageQuery From(IQueryCollection query)
    {
        var duplicated = new[]
            {
                OrderReportFilters.PresetParameter, OrderReportFilters.StartDateParameter,
                OrderReportFilters.EndDateParameter, OrderReportFilters.PlatformParameter
            }
            .Where(p => query.TryGetValue(p, out var values) && values.Count > 1)
            .ToList();

        var preset = First(query, OrderReportFilters.PresetParameter);
        var start = First(query, OrderReportFilters.StartDateParameter);
        var end = First(query, OrderReportFilters.EndDateParameter);
        if (string.IsNullOrEmpty(preset))
        {
            preset = string.IsNullOrEmpty(start) && string.IsNullOrEmpty(end)
                ? DefaultPreset
                : OrderReportFilters.PresetValue(OrderReportPreset.Custom);
        }

        var section = First(query, SectionParameter);
        return new ReportPageQuery(
            preset,
            start,
            end,
            First(query, OrderReportFilters.PlatformParameter),
            First(query, CompareParameter) == "1",
            ReportSections.All.Contains(section ?? "", StringComparer.Ordinal) ? section! : ReportSections.Overview,
            duplicated);
    }

    /// <summary>This state with another section.</summary>
    public string UrlForSection(string section) => (this with { Section = section }).Url();

    /// <summary>This state with a preset period; preset periods carry no dates.</summary>
    public string UrlForPreset(string preset) => (this with { Preset = preset, StartDate = null, EndDate = null }).Url();

    public string UrlWithoutComparison() => (this with { Compare = false }).Url();

    /// <summary>The page default for the current section.</summary>
    public string ResetUrl() => (this with { Preset = DefaultPreset, StartDate = null, EndDate = null, Platform = null, Compare = false }).Url();

    public string UrlForCustomRange(DateOnly start, DateOnly end, bool compare) => (this with
    {
        Preset = OrderReportFilters.PresetValue(OrderReportPreset.Custom),
        StartDate = Application.Orders.OrdersReceivedAtQueryRange.FormatWebDate(start),
        EndDate = Application.Orders.OrdersReceivedAtQueryRange.FormatWebDate(end),
        Compare = compare
    }).Url();

    /// <summary>The page URL of this state, defaults left out.</summary>
    public string Url() => PagePath + QueryString();

    public string QueryString()
    {
        var pairs = new List<KeyValuePair<string, string?>>();
        if (!string.Equals(Preset, DefaultPreset, StringComparison.OrdinalIgnoreCase))
            pairs.Add(new(OrderReportFilters.PresetParameter, Preset));
        if (IsCustom)
        {
            pairs.Add(new(OrderReportFilters.StartDateParameter, StartDate ?? ""));
            pairs.Add(new(OrderReportFilters.EndDateParameter, EndDate ?? ""));
        }

        if (!string.IsNullOrEmpty(Platform))
            pairs.Add(new(OrderReportFilters.PlatformParameter, Platform));
        if (Compare)
            pairs.Add(new(CompareParameter, "1"));
        if (Section != ReportSections.Overview)
            pairs.Add(new(SectionParameter, Section));
        return Microsoft.AspNetCore.Http.QueryString.Create(pairs).ToUriComponent();
    }

    private static string? First(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
