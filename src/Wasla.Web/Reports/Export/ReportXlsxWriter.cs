using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Wasla.Web.Reports.Export;

/// <summary>
/// The Excel workbook of a report export (WAS-184): an Office Open XML workbook (.xlsx) written with the Open XML SDK, with
/// three sheets named in the user's language: the summary (title, details, measures with their definitions, notes), the
/// platform rows and the day rows. Counts, amounts and rates are number cells with number formats, days and the generation
/// time are date cells, and every label is an inline string cell in Text format (<c>@</c>), so nothing is ever a formula.
/// The workbook has no formulas, macros, external links or shared strings. Data sheets have a frozen header row and first
/// column and a filter; sheets read right to left in Arabic.
/// </summary>
public static class ReportXlsxWriter
{
    /// <summary>The longest text Excel keeps in one cell.</summary>
    private const int MaxCellText = 32767;

    private const uint StyleHeader = 1;
    private const uint StyleText = 2;
    private const uint StyleCount = 3;
    private const uint StyleMoney = 4;
    private const uint StyleRate = 5;
    private const uint StyleDate = 6;
    private const uint StyleDateTime = 7;
    private const uint StyleTitle = 8;
    private const uint StyleWrap = 9;
    private const uint StyleLabel = 10;

    public static byte[] Write(ReportExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        using (var package = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = package.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            workbookPart.AddNewPart<WorkbookStylesPart>().Stylesheet = Styles();

            var names = SheetNames([document.Summary.Title, document.Platforms.Title, document.Days.Title]);
            var sheets = new Sheets();
            var definedNames = new DefinedNames();
            workbookPart.Workbook.Append(new BookViews(new WorkbookView { ActiveTab = 0U }), sheets);

            AddSheet(workbookPart, sheets, names[0], SummarySheet(document, selected: true));
            AddSheet(workbookPart, sheets, names[1], DataSheet(document.Platforms, document.IsRightToLeft, names[1], 1, definedNames));
            AddSheet(workbookPart, sheets, names[2], DataSheet(document.Days, document.IsRightToLeft, names[2], 2, definedNames));
            workbookPart.Workbook.Append(definedNames);

            package.PackageProperties.Title = document.Title;
            package.PackageProperties.Creator = "Wasla";
            package.PackageProperties.Language = document.Culture.Name;
            if (document.Details.Select(d => d.Value.Moment).FirstOrDefault(m => m is not null) is { } generated)
                package.PackageProperties.Created = generated.UtcDateTime;
        }

        return stream.ToArray();
    }

    private static void AddSheet(WorkbookPart workbookPart, Sheets sheets, string name, Worksheet worksheet)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        part.Worksheet = worksheet;
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = (uint)sheets.ChildElements.Count + 1,
            Name = name
        });
    }

    private static Worksheet SummarySheet(ReportExportDocument document, bool selected)
    {
        var rows = new List<Row>();
        var index = 1U;

        rows.Add(new Row(TextCell(1, index, document.Title, StyleTitle)) { RowIndex = index });
        index += 2;
        foreach (var detail in document.Details)
        {
            rows.Add(new Row(TextCell(1, index, detail.Label, StyleLabel), ValueCell(2, index, detail.Value)) { RowIndex = index });
            index++;
        }

        index++;
        rows.Add(new Row(TextCell(1, index, document.Summary.Title, StyleLabel)) { RowIndex = index });
        index++;
        rows.Add(HeaderRow(document.Summary.Columns, index));
        index++;
        foreach (var row in document.Summary.Rows)
        {
            rows.Add(new Row(
                TextCell(1, index, row[0].Text ?? "", StyleText),
                ValueCell(2, index, row[1]),
                TextCell(3, index, row[2].Text ?? "", StyleWrap)) { RowIndex = index });
            index++;
        }

        index++;
        rows.Add(new Row(TextCell(1, index, document.NotesTitle, StyleLabel)) { RowIndex = index });
        index++;
        foreach (var note in document.Notes)
        {
            rows.Add(new Row(TextCell(1, index, note, StyleText)) { RowIndex = index });
            index++;
        }

        var labelWidth = document.Details.Select(d => d.Label.Length)
            .Concat(document.Summary.Rows.Select(r => (r[0].Text ?? "").Length))
            .DefaultIfEmpty(10).Max();
        return new Worksheet(
            new SheetViews(new SheetView { WorkbookViewId = 0U, TabSelected = selected, RightToLeft = document.IsRightToLeft }),
            new Columns(
                Column(1, Math.Clamp(labelWidth + 3, 18, 60)),
                Column(2, 26),
                Column(3, 100)),
            new SheetData(rows));
    }

    private static Worksheet DataSheet(ReportExportTable table, bool rightToLeft, string sheetName, uint sheetIndex, DefinedNames definedNames)
    {
        var rows = new List<Row> { HeaderRow(table.Columns, 1) };
        var index = 2U;
        foreach (var row in table.Rows)
        {
            rows.Add(new Row(row.Select((cell, i) => ValueCell(i + 1, index, cell))) { RowIndex = index });
            index++;
        }

        var lastColumn = ColumnName(table.Columns.Count);
        var range = $"A1:{lastColumn}{Math.Max(1, table.Rows.Count + 1)}";
        definedNames.Append(new DefinedName
        {
            Name = "_xlnm._FilterDatabase",
            LocalSheetId = sheetIndex,
            Hidden = true,
            Text = $"'{sheetName.Replace("'", "''", StringComparison.Ordinal)}'!$A$1:${lastColumn}${Math.Max(1, table.Rows.Count + 1)}"
        });

        var columns = new Columns(table.Columns.Select((column, i) =>
        {
            var longest = table.Rows.Select(r => r[i].Display.Length).Append(Math.Min(column.Header.Length, 24)).Max();
            return Column(i + 1, Math.Clamp(longest + 3, 12, 40));
        }));

        return new Worksheet(
            new SheetViews(new SheetView(
                new Pane
                {
                    HorizontalSplit = 1D,
                    VerticalSplit = 1D,
                    TopLeftCell = "B2",
                    ActivePane = PaneValues.BottomRight,
                    State = PaneStateValues.Frozen
                },
                new Selection { Pane = PaneValues.BottomRight, ActiveCell = "B2", SequenceOfReferences = new ListValue<StringValue> { InnerText = "B2" } })
            {
                WorkbookViewId = 0U,
                RightToLeft = rightToLeft
            }),
            columns,
            new SheetData(rows),
            new AutoFilter { Reference = range });
    }

    private static Row HeaderRow(IReadOnlyList<ReportExportColumn> columns, uint index) =>
        new(columns.Select((column, i) => TextCell(i + 1, index, column.Header, StyleHeader))) { RowIndex = index };

    private static Cell ValueCell(int column, uint row, ReportExportCell cell)
    {
        var reference = ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);
        if (cell.Kind == ReportExportValueKind.Text)
            return TextCell(column, row, cell.Text ?? "", StyleText);

        var style = cell.Kind switch
        {
            ReportExportValueKind.Count => StyleCount,
            ReportExportValueKind.Money => StyleMoney,
            ReportExportValueKind.Rate => StyleRate,
            ReportExportValueKind.Date => StyleDate,
            _ => StyleDateTime
        };

        // A figure that is not defined stays an empty cell, never 0.
        if (!cell.HasValue)
            return new Cell { CellReference = reference, StyleIndex = style };

        var value = cell.Kind switch
        {
            ReportExportValueKind.Date => cell.Date!.Value.ToDateTime(TimeOnly.MinValue).ToOADate().ToString("R", CultureInfo.InvariantCulture),
            ReportExportValueKind.DateTime => cell.Moment!.Value.DateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture),
            _ => cell.Number!.Value.ToString(CultureInfo.InvariantCulture)
        };

        return new Cell
        {
            CellReference = reference,
            DataType = CellValues.Number,
            StyleIndex = style,
            CellValue = new CellValue(value)
        };
    }

    private static Cell TextCell(int column, uint row, string text, uint style)
    {
        var value = text.Length > MaxCellText ? text[..MaxCellText] : text;
        return new Cell
        {
            CellReference = ColumnName(column) + row.ToString(CultureInfo.InvariantCulture),
            DataType = CellValues.InlineString,
            StyleIndex = style,
            InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
        };
    }

    private static Column Column(int index, double width) => new()
    {
        Min = (uint)index,
        Max = (uint)index,
        Width = width,
        CustomWidth = true
    };

    /// <summary>A1-style column letters: 1 is A, 27 is AA.</summary>
    public static string ColumnName(int column)
    {
        var name = "";
        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            name = (char)('A' + remainder) + name;
            column = (column - 1) / 26;
        }

        return name;
    }

    /// <summary>
    /// Sheet names Excel accepts: none of <c>[ ] : * ? / \</c>, no apostrophe at either end, at most 31 characters, unique
    /// and not empty.
    /// </summary>
    public static IReadOnlyList<string> SheetNames(IReadOnlyList<string> titles)
    {
        var names = new List<string>();
        for (var i = 0; i < titles.Count; i++)
        {
            var name = new string(ReportExportText.Clean(titles[i]).Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray())
                .Trim().Trim('\'');
            if (name.Length > 31)
                name = name[..31];
            if (name.Length == 0 || names.Contains(name, StringComparer.OrdinalIgnoreCase))
                name = "Sheet" + (i + 1).ToString(CultureInfo.InvariantCulture);
            names.Add(name);
        }

        return names;
    }

    private static Stylesheet Styles()
    {
        var border = BorderStyle("D3CABD");
        return new Stylesheet(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164U, FormatCode = "#,##0.00" },
                new NumberingFormat { NumberFormatId = 165U, FormatCode = "0.0%" },
                new NumberingFormat { NumberFormatId = 166U, FormatCode = "yyyy-mm-dd" },
                new NumberingFormat { NumberFormatId = 167U, FormatCode = "yyyy-mm-dd hh:mm" })
            { Count = 4U },
            new Fonts(
                new Font(new FontSize { Val = 11D }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11D }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 14D }, new FontName { Val = "Calibri" }))
            { Count = 3U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFF4F0E9" }, new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid }))
            { Count = 3U },
            new Borders(
                new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
                border)
            { Count = 2U },
            new CellStyleFormats(new CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U }) { Count = 1U },
            new CellFormats(
                new CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U },
                new CellFormat(new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Center })
                {
                    NumberFormatId = 49U, FontId = 1U, FillId = 2U, BorderId = 1U, FormatId = 0U,
                    ApplyNumberFormat = true, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true
                },
                new CellFormat { NumberFormatId = 49U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 3U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 164U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 165U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 166U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 167U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 49U, FontId = 2U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true, ApplyFont = true },
                new CellFormat(new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top })
                {
                    NumberFormatId = 49U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true, ApplyAlignment = true
                },
                new CellFormat { NumberFormatId = 49U, FontId = 1U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true, ApplyFont = true })
            { Count = 11U },
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U });
    }

    private static Border BorderStyle(string rgb) => new(
        new LeftBorder(),
        new RightBorder(),
        new TopBorder(),
        new BottomBorder(new Color { Rgb = "FF" + rgb }) { Style = BorderStyleValues.Thin },
        new DiagonalBorder());
}
