using System.Globalization;
using System.Text;
using Wasla.Application.Abstractions.Reports;
using Wasla.Web.Models.Reports;

namespace Wasla.Web.Reports.Export;

/// <summary>What a value is, so each format can store it as its own type (a number, a date or text).</summary>
public enum ReportExportValueKind
{
    Text,
    Date,
    DateTime,
    Count,
    Money,
    Rate
}

/// <summary>
/// One value of the export. <see cref="Display"/> is the value as the report page writes it in the request culture (the
/// PDF prints it); CSV and Excel store <see cref="Number"/>, <see cref="Date"/>, <see cref="Moment"/> or <see cref="Text"/>
/// as typed values instead. A figure that is not defined (an average without delivered orders, a rate without orders) has
/// no value and is never 0.
/// </summary>
public readonly record struct ReportExportCell
{
    private ReportExportCell(ReportExportValueKind kind, string display, string? text, decimal? number, DateOnly? date, DateTimeOffset? moment)
    {
        Kind = kind;
        Display = display;
        Text = text;
        Number = number;
        Date = date;
        Moment = moment;
    }

    public ReportExportValueKind Kind { get; }

    public string Display { get; }

    public string? Text { get; }

    public decimal? Number { get; }

    public DateOnly? Date { get; }

    public DateTimeOffset? Moment { get; }

    public bool HasValue => Kind switch
    {
        ReportExportValueKind.Text => Text is not null,
        ReportExportValueKind.Date => Date is not null,
        ReportExportValueKind.DateTime => Moment is not null,
        _ => Number is not null
    };

    /// <summary>Text from any source, restaurant names included; see <see cref="ReportExportText.Clean"/>.</summary>
    public static ReportExportCell OfText(string text)
    {
        var clean = ReportExportText.Clean(text);
        return new(ReportExportValueKind.Text, clean, clean, null, null, null);
    }

    public static ReportExportCell OfDate(DateOnly date, string display) => new(ReportExportValueKind.Date, display, null, null, date, null);

    public static ReportExportCell OfMoment(DateTimeOffset moment, string display) => new(ReportExportValueKind.DateTime, display, null, null, null, moment);

    public static ReportExportCell OfCount(int value) => new(ReportExportValueKind.Count, ReportFormat.Count(value), null, value, null, null);

    public static ReportExportCell OfMoney(decimal? value) => new(ReportExportValueKind.Money, ReportFormat.Money(value), null, value, null, null);

    /// <summary>A fraction from 0 to 1, written as a percentage with one decimal as on the page.</summary>
    public static ReportExportCell OfRate(decimal? fraction) => new(ReportExportValueKind.Rate, ReportFormat.Percent(fraction), null, fraction, null, null);
}

public sealed record ReportExportColumn(string Header, ReportExportValueKind Kind);

/// <summary>A titled table; every row has one cell per column.</summary>
public sealed record ReportExportTable(string Title, IReadOnlyList<ReportExportColumn> Columns, IReadOnlyList<IReadOnlyList<ReportExportCell>> Rows);

/// <summary>A labelled fact about the export: restaurant, period, platform filter, currency, time zone, generation time.</summary>
public sealed record ReportExportDetail(string Label, ReportExportCell Value);

/// <summary>
/// The report as every export format writes it (WAS-184): built once from one <see cref="OrderReport"/>, so the CSV, Excel
/// and PDF files of a request hold the same rows and the same figures. Every figure is a field of that report; nothing is
/// calculated here. Labels are in the request culture.
/// </summary>
public sealed class ReportExportDocument
{
    public required OrderReport Report { get; init; }

    public required CultureInfo Culture { get; init; }

    public required bool IsRightToLeft { get; init; }

    public required string Title { get; init; }

    public required string RestaurantName { get; init; }

    public required IReadOnlyList<ReportExportDetail> Details { get; init; }

    /// <summary>Measure, value and definition of every summary figure.</summary>
    public required ReportExportTable Summary { get; init; }

    public required ReportExportTable Platforms { get; init; }

    public required ReportExportTable Days { get; init; }

    public required string NotesTitle { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>What only the PDF draws: headline figures, charts and page furniture.</summary>
    public required ReportExportPrintContent Print { get; init; }
}

/// <summary>A headline figure of the PDF's first page, as the report page shows it.</summary>
public sealed record ReportExportHeadline(string Label, string Value, string? Hint);

/// <summary>A legend entry: a chart series and its label.</summary>
public sealed record ReportExportLegendItem(string Series, string Label);

/// <summary>A daily chart of the report page, with the page's own columns, segments and axis.</summary>
public sealed record ReportExportChart(string Title, ReportBarChart Chart);

/// <summary>A platform's share of the orders, as the Overview's platform share list draws it.</summary>
public sealed record ReportExportShare(string Platform, string Series, string Count, string Share, double BarPercent);

public sealed record ReportExportPrintContent(
    string Subtitle,
    string PageNumberFormat,
    IReadOnlyList<ReportExportHeadline> Headlines,
    string? DailyTitle,
    string? DailyHint,
    IReadOnlyList<ReportExportLegendItem> Legend,
    IReadOnlyList<ReportExportChart> Charts,
    string? SingleDayNotice,
    string? ShareTitle,
    string? ShareHint,
    IReadOnlyList<ReportExportShare> Shares,
    IReadOnlyList<string> Notices,
    string NoRowsText);

public static class ReportExportText
{
    /// <summary>
    /// Text as the export stores it: control characters (line breaks included) become spaces, an ill-formed UTF-16
    /// sequence and the noncharacters U+FFFE and U+FFFF become U+FFFD, and the ends are trimmed. Every format can then hold
    /// it as plain text: XML allows it, a CSV field keeps one line, and a PDF label stays on its baseline.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
                builder.Append(' ');
            else if (rune.Value is 0xFFFE or 0xFFFF)
                builder.Append(Rune.ReplacementChar.ToString());
            else
                builder.Append(rune.ToString());
        }

        return builder.ToString().Trim();
    }
}
