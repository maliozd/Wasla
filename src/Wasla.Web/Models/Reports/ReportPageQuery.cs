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
/// they are valid. An accepted filter is then written in its one canonical form (<see cref="Canonicalized"/>), and a refused
/// one stays exactly as submitted, repeated values included, so no link can repair it. <c>reports-page.js</c> takes this
/// state from the server's answer and builds the same URLs (<c>toQuery</c>), so reload, back and tab changes keep them.
/// </summary>
public sealed record ReportPageQuery(
    string Preset,
    string? StartDate,
    string? EndDate,
    string? Platform,
    bool Compare,
    string Section,
    IReadOnlyDictionary<string, IReadOnlyList<string?>> RepeatedFilters)
{
    /// <summary>The page's own default period; the Dashboard already covers today.</summary>
    public const string DefaultPreset = "last30days";

    public const string CompareParameter = "compare";
    public const string SectionParameter = "section";

    public const string PagePath = "/reports";

    private static readonly string[] FilterParameters =
    [
        OrderReportFilters.PresetParameter, OrderReportFilters.StartDateParameter,
        OrderReportFilters.EndDateParameter, OrderReportFilters.PlatformParameter
    ];

    private static readonly string[] PeriodParameters =
        [OrderReportFilters.PresetParameter, OrderReportFilters.StartDateParameter, OrderReportFilters.EndDateParameter];

    /// <summary>The filter parameters given more than once; such a query is refused.</summary>
    public IReadOnlyList<string> DuplicatedParameters => RepeatedFilters.Keys.ToList();

    public bool IsCustom => string.Equals(Preset, OrderReportFilters.PresetValue(OrderReportPreset.Custom), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when something differs from the page default, so the reset action applies.</summary>
    public bool HasNonDefaultFilter =>
        !string.Equals(Preset, DefaultPreset, StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Platform)
        || Compare;

    public static ReportPageQuery From(IQueryCollection query)
    {
        var repeated = FilterParameters
            .Where(p => query.TryGetValue(p, out var values) && values.Count > 1)
            .ToDictionary(p => p, p => (IReadOnlyList<string?>)query[p].ToArray());

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
            repeated);
    }

    /// <summary>
    /// This state in its one canonical form, from the filter <see cref="OrderReportFilters"/> accepted: lowercase preset,
    /// platform name, dates only for a custom period. Nothing that was refused can reach this.
    /// </summary>
    public ReportPageQuery Canonicalized(OrderReportFilter filter)
    {
        var isCustom = filter.Period.Preset == OrderReportPreset.Custom;
        return this with
        {
            Preset = OrderReportFilters.PresetValue(filter.Period.Preset),
            StartDate = isCustom ? Application.Orders.OrdersReceivedAtQueryRange.FormatWebDate(filter.Period.StartDate) : null,
            EndDate = isCustom ? Application.Orders.OrdersReceivedAtQueryRange.FormatWebDate(filter.Period.EndDate) : null,
            Platform = filter.Platform?.ToString()
        };
    }

    /// <summary>This state with another section.</summary>
    public string UrlForSection(string section) => (this with { Section = section }).Url();

    /// <summary>This state with a preset period; preset periods carry no dates.</summary>
    public string UrlForPreset(string preset) => (this with
    {
        Preset = preset,
        StartDate = null,
        EndDate = null,
        RepeatedFilters = RepeatedFilters.Where(r => !PeriodParameters.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value)
    }).Url();

    public string UrlWithoutComparison() => (this with { Compare = false }).Url();

    /// <summary>The page default for the current section.</summary>
    public string ResetUrl() => (this with
    {
        Preset = DefaultPreset,
        StartDate = null,
        EndDate = null,
        Platform = null,
        Compare = false,
        RepeatedFilters = new Dictionary<string, IReadOnlyList<string?>>()
    }).Url();

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

        void Filter(string name, string? value, bool keepEmpty = false)
        {
            if (RepeatedFilters.TryGetValue(name, out var values))
                pairs.AddRange(values.Select(v => new KeyValuePair<string, string?>(name, v)));
            else if (keepEmpty || !string.IsNullOrEmpty(value))
                pairs.Add(new(name, value ?? ""));
        }

        Filter(OrderReportFilters.PresetParameter, string.Equals(Preset, DefaultPreset, StringComparison.OrdinalIgnoreCase) ? null : Preset);
        Filter(OrderReportFilters.StartDateParameter, StartDate, keepEmpty: IsCustom);
        Filter(OrderReportFilters.EndDateParameter, EndDate, keepEmpty: IsCustom);
        Filter(OrderReportFilters.PlatformParameter, Platform);
        if (Compare)
            pairs.Add(new(CompareParameter, "1"));
        if (Section != ReportSections.Overview)
            pairs.Add(new(SectionParameter, Section));
        return Microsoft.AspNetCore.Http.QueryString.Create(pairs).ToUriComponent();
    }

    private static string? First(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
