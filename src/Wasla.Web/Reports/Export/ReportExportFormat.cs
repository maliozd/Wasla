namespace Wasla.Web.Reports.Export;

/// <summary>The file formats of the report export (WAS-184). Each carries the same document, see docs/orders/reporting.md#export.</summary>
public enum ReportExportFormat
{
    Csv,
    Pdf,
    Xlsx
}

public static class ReportExportFormats
{
    /// <summary>The query parameter that chooses the format.</summary>
    public const string Parameter = "format";

    public static IReadOnlyList<ReportExportFormat> All { get; } = [ReportExportFormat.Csv, ReportExportFormat.Pdf, ReportExportFormat.Xlsx];

    /// <summary>The query-string value of a format.</summary>
    public static string Value(ReportExportFormat format) => format switch
    {
        ReportExportFormat.Csv => "csv",
        ReportExportFormat.Pdf => "pdf",
        ReportExportFormat.Xlsx => "xlsx",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    /// <summary>A format named exactly by its value, in any letter case. Anything else, empty included, is refused.</summary>
    public static bool TryParse(string? value, out ReportExportFormat format)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(Value(candidate), value, StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    public static string ContentType(ReportExportFormat format) => format switch
    {
        ReportExportFormat.Csv => "text/csv; charset=utf-8",
        ReportExportFormat.Pdf => "application/pdf",
        ReportExportFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    /// <summary>
    /// The download name: ASCII only, from the period and platform the server accepted, so no restaurant or user text can
    /// reach a header. For example <c>wasla-orders-report_2026-10-12_2026-11-10.pdf</c>.
    /// </summary>
    public static string FileName(ReportExportDocument document, ReportExportFormat format)
    {
        var period = document.Report.Filter.Period;
        var platform = document.Report.Filter.Platform is { } p ? "_" + p.ToString().ToLowerInvariant() : "";
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"wasla-orders-report_{period.StartDate:yyyy-MM-dd}_{period.EndDate:yyyy-MM-dd}{platform}.{Value(format)}");
    }
}
