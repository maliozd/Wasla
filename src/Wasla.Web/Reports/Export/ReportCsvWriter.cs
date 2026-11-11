using System.Globalization;
using System.Text;

namespace Wasla.Web.Reports.Export;

/// <summary>
/// The CSV of a report export (WAS-184). The format is fixed and does not follow the user's culture
/// (docs/orders/reporting.md#csv-format):
/// <list type="bullet">
/// <item>UTF-8 with a byte order mark, CRLF line ends, comma delimiter, RFC 4180 quoting: a field with a comma, a double
/// quote or a line break is enclosed in double quotes and its double quotes are doubled.</item>
/// <item>Sections in this order, separated by an empty line: the title and the details (label, value), the summary
/// (measure, value, definition), the platform rows, the day rows (oldest first), then the notes. Labels are in the
/// user's language.</item>
/// <item>Counts are integers, amounts have two decimals with a dot and no grouping, a rate is a fraction from 0 to 1 with
/// four decimals, dates are <c>yyyy-MM-dd</c> and the generation time is ISO 8601 with its offset. A figure that is not
/// defined is an empty field, never 0.</item>
/// <item>Text that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a line break (or their full-width forms),
/// after any leading spaces, is prefixed with an apostrophe so no spreadsheet runs it as a formula. Numbers are written as
/// numbers and never prefixed.</item>
/// </list>
/// </summary>
public static class ReportCsvWriter
{
    public const char Delimiter = ',';
    private const string LineBreak = "\r\n";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static byte[] Write(ReportExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var csv = new StringBuilder();
        Line(csv, Text(document.Title));
        foreach (var detail in document.Details)
            Line(csv, Text(detail.Label), Value(detail.Value));

        foreach (var table in new[] { document.Summary, document.Platforms, document.Days })
        {
            csv.Append(LineBreak);
            Line(csv, Text(table.Title));
            Line(csv, table.Columns.Select(c => Text(c.Header)).ToArray());
            foreach (var row in table.Rows)
                Line(csv, row.Select(Value).ToArray());
        }

        csv.Append(LineBreak);
        Line(csv, Text(document.NotesTitle));
        foreach (var note in document.Notes)
            Line(csv, Text(note));

        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(csv.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>A field of one value, quoted when needed.</summary>
    public static string Value(ReportExportCell cell)
    {
        if (!cell.HasValue)
            return "";

        return cell.Kind switch
        {
            ReportExportValueKind.Text => Text(cell.Text!),
            ReportExportValueKind.Date => cell.Date!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ReportExportValueKind.DateTime => cell.Moment!.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ReportExportValueKind.Count => cell.Number!.Value.ToString("0", CultureInfo.InvariantCulture),
            ReportExportValueKind.Money => cell.Number!.Value.ToString("0.00", CultureInfo.InvariantCulture),
            ReportExportValueKind.Rate => Math.Round(cell.Number!.Value, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell.Kind, null)
        };
    }

    /// <summary>A text field: neutralised against formulas, then quoted when needed.</summary>
    public static string Text(string value) => Quote(Neutralize(value));

    /// <summary>
    /// Text a spreadsheet could read as a formula gets a leading apostrophe; anything else is unchanged (OWASP CSV
    /// injection guidance).
    /// </summary>
    public static string Neutralize(string value)
    {
        foreach (var c in value)
        {
            if (c is '\t' or '\r' or '\n')
                return "'" + value;
            if (char.IsWhiteSpace(c))
                continue;
            return c is '=' or '+' or '-' or '@' or '＝' or '＋' or '－' or '＠'
                ? "'" + value
                : value;
        }

        return value;
    }

    private static string Quote(string value) =>
        value.IndexOfAny([Delimiter, '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    private static void Line(StringBuilder csv, params string[] fields)
    {
        csv.AppendJoin(Delimiter, fields);
        csv.Append(LineBreak);
    }
}
