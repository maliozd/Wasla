using System.Text.RegularExpressions;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Wasla.Web.Models.Reports;

namespace Wasla.Web.Reports.Export;

/// <summary>
/// The printable PDF of a report export (WAS-184), drawn with QuestPDF from the export document: the report's details and
/// notices, its headline figures, the Overview's daily charts and platform share, and the summary, platform and daily
/// tables. Text is laid out and shaped by the library (HarfBuzz) with the fonts registered here only: Lato for Latin,
/// Turkish and Cyrillic, Noto Sans Arabic for Arabic. Nothing in the document is parsed as markup, nothing is loaded from
/// outside the assembly, and system fonts are not used, so the output does not depend on the server.
/// </summary>
public static partial class ReportPdfWriter
{
    public const string LatinFontFamily = "Lato";
    public const string ArabicFontFamily = "Noto Sans Arabic";

    internal static readonly string[] FontResources =
    [
        "Wasla.Web.Reports.Export.Fonts.NotoSansArabic-Regular.ttf",
        "Wasla.Web.Reports.Export.Fonts.NotoSansArabic-Bold.ttf"
    ];

    // Light tokens of wasla-foundation.css and wasla-reports.css: a print is always light.
    private static readonly Color Ink = Color.FromHex("#28231F");
    private static readonly Color Muted = Color.FromHex("#746D66");
    private static readonly Color Border = Color.FromHex("#E3DDD3");
    private static readonly Color BorderStrong = Color.FromHex("#D3CABD");
    private static readonly Color SurfaceMuted = Color.FromHex("#F4F0E9");
    private static readonly Color Grid = Color.FromHex("#EDE8DF");
    private static readonly Color InfoSoft = Color.FromHex("#E7EDF4");
    private static readonly Color Info = Color.FromHex("#356599");
    private static readonly Color PartialBorder = Color.FromHex("#C84B0F");

    private const float ChartHeight = 96f;
    private const float TickLabelHeight = 9f;

    private static readonly Lazy<bool> Configured = new(Configure, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Registers the bundled fonts and the library settings once per process. The QuestPDF Community license applies to
    /// organisations below its revenue threshold; see docs/orders/reporting.md#export.
    /// </summary>
    public static void EnsureConfigured() => _ = Configured.Value;

    public static byte[] Write(ReportExportDocument document) => Compose(document).GeneratePdf();

    /// <summary>The PDF's layout, before it is written; tests also rasterize its pages to look at them.</summary>
    public static IDocument Compose(ReportExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureConfigured();

        var generated = document.Details
            .Select(d => d.Value.Moment)
            .FirstOrDefault(m => m is not null) ?? DateTimeOffset.UtcNow;

        return Document
            .Create(container => container.Page(page => Page(page, document)))
            .WithMetadata(new DocumentMetadata
            {
                Title = $"{document.Title} · {document.Print.Subtitle}",
                Author = document.RestaurantName,
                Creator = "Wasla",
                Producer = "Wasla",
                Language = document.Culture.Name,
                CreationDate = generated,
                ModifiedDate = generated
            });
    }

    private static bool Configure()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // Only the registered fonts, so a server without fonts renders the same file.
        QuestPDF.Settings.UseSystemFonts = false;
        // A restaurant name can hold characters no bundled font has (an emoji, for example). Such a glyph is drawn as a
        // placeholder instead of failing the whole export; ReportPdfWriterTests checks that every label and figure has glyphs.
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;

        var assembly = typeof(ReportPdfWriter).Assembly;
        foreach (var name in FontResources)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The embedded font {name} is missing.");
            FontManager.RegisterFontFromStream(stream);
        }

        return true;
    }

    private static void Page(PageDescriptor page, ReportExportDocument document)
    {
        page.Size(PageSizes.A4.Landscape());
        page.Margin(30);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(style => style.FontFamily(LatinFontFamily, ArabicFontFamily).FontSize(8.5f).FontColor(Ink).LineHeight(1.2f));
        if (document.IsRightToLeft)
            page.ContentFromRightToLeft();

        page.Header().Element(c => Header(c, document));
        page.Content().PaddingVertical(10).Column(column =>
        {
            column.Spacing(12);
            column.Item().Element(c => Details(c, document));
            foreach (var notice in document.Print.Notices)
                column.Item().Element(c => Notice(c, notice));
            column.Item().ShowEntire().Element(c => Headlines(c, document.Print.Headlines));
            column.Item().Element(c => Daily(c, document.Print));
            if (document.Print.Shares.Count > 0)
                column.Item().ShowEntire().Element(c => Shares(c, document.Print));
            column.Item().Element(c => Table(c, document.Summary, SummaryWidths, document.Print.NoRowsText));
            column.Item().Element(c => Table(c, document.Platforms, FigureWidths(document.Platforms), document.Print.NoRowsText));
            column.Item().Element(c => Table(c, document.Days, FigureWidths(document.Days), document.Print.NoRowsText));
            column.Item().Element(c => Notes(c, document));
        });
        page.Footer().Element(c => Footer(c, document));
    }

    private static void Header(IContainer container, ReportExportDocument document) =>
        container.BorderBottom(0.75f).BorderColor(BorderStrong).PaddingBottom(6).Row(row =>
        {
            row.Spacing(12);
            row.RelativeItem().Column(column =>
            {
                column.Item().Text(document.Title).FontSize(13).Bold();
                column.Item().Text(document.RestaurantName).FontSize(9).FontColor(Muted);
            });
            row.RelativeItem().AlignBottom().Text(document.Print.Subtitle).FontSize(9).SemiBold().AlignEnd();
        });

    private static void Footer(IContainer container, ReportExportDocument document) =>
        container.BorderTop(0.5f).BorderColor(Border).PaddingTop(4).Row(row =>
        {
            row.Spacing(12);
            // The generation time on every printed page; the restaurant and period are in the header.
            var created = document.Details.FirstOrDefault(d => d.Value.Kind == ReportExportValueKind.DateTime);
            row.RelativeItem().Text(created is null ? document.Title : $"{document.Title} · {created.Label}: {created.Value.Display}").FontSize(7).FontColor(Muted);
            row.AutoItem().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(7).FontColor(Muted));
                PageNumber(text, document.Print.PageNumberFormat);
            });
        });

    /// <summary>Writes "Page {0} of {1}" in the document's language, with the library's own page counters in the slots.</summary>
    private static void PageNumber(TextDescriptor text, string format)
    {
        foreach (var part in PageNumberSlots().Split(format))
        {
            if (part == "{0}")
                text.CurrentPageNumber();
            else if (part == "{1}")
                text.TotalPages();
            else if (part.Length > 0)
                text.Span(part);
        }
    }

    [GeneratedRegex(@"(\{0\}|\{1\})")]
    private static partial Regex PageNumberSlots();

    private static void Details(IContainer container, ReportExportDocument document)
    {
        var details = document.Details;
        var half = (details.Count + 1) / 2;
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1.1f);
                columns.RelativeColumn(1.6f);
                columns.ConstantColumn(16);
                columns.RelativeColumn(1.1f);
                columns.RelativeColumn(1.6f);
            });

            for (var i = 0; i < half; i++)
            {
                DetailCells(table, details[i]);
                table.Cell();
                if (i + half < details.Count)
                    DetailCells(table, details[i + half]);
                else
                {
                    table.Cell();
                    table.Cell();
                }
            }
        });
    }

    private static void DetailCells(TableDescriptor table, ReportExportDetail detail)
    {
        table.Cell().BorderBottom(0.5f).BorderColor(Border).PaddingVertical(2.5f).Text(detail.Label).FontColor(Muted);
        table.Cell().BorderBottom(0.5f).BorderColor(Border).PaddingVertical(2.5f).Text(detail.Value.Display).SemiBold();
    }

    private static void Notice(IContainer container, string text) =>
        container.Background(InfoSoft).CornerRadius(3).PaddingVertical(5).PaddingHorizontal(8)
            .Text(text).FontColor(Info);

    private static void Headlines(IContainer container, IReadOnlyList<ReportExportHeadline> headlines) =>
        container.Row(row =>
        {
            row.Spacing(8);
            foreach (var headline in headlines)
            {
                row.RelativeItem().Border(0.75f).BorderColor(Border).CornerRadius(4).Padding(9).Column(column =>
                {
                    column.Item().Text(headline.Label).FontSize(8).SemiBold().FontColor(Muted);
                    column.Item().PaddingTop(2).Text(headline.Value).FontSize(15).Bold();
                    if (headline.Hint is { } hint)
                        column.Item().PaddingTop(1).Text(hint).FontSize(7).FontColor(Muted);
                });
            }
        });

    private static void Daily(IContainer container, ReportExportPrintContent print) =>
        container.Column(column =>
        {
            column.Spacing(4);
            if (print.SingleDayNotice is { } single)
            {
                column.Item().Element(c => Notice(c, single));
                return;
            }

            // The heading and legend stay on the page of the first chart; each chart is kept whole.
            for (var i = 0; i < print.Charts.Count; i++)
            {
                var chart = print.Charts[i];
                var first = i == 0;
                column.Item().ShowEntire().Column(block =>
                {
                    block.Spacing(4);
                    if (first)
                    {
                        block.Item().Element(c => SectionTitle(c, print.DailyTitle!, print.DailyHint));
                        block.Item().Element(c => Legend(c, print.Legend));
                    }

                    block.Item().PaddingTop(4).Text(chart.Title).FontSize(7.5f).SemiBold().FontColor(Muted);
                    block.Item().Element(c => BarChart(c, chart.Chart));
                });
            }
        });

    private static void Legend(IContainer container, IReadOnlyList<ReportExportLegendItem> legend) =>
        container.Row(row =>
        {
            row.Spacing(12);
            foreach (var item in legend)
            {
                row.AutoItem().Row(entry =>
                {
                    entry.Spacing(4);
                    entry.AutoItem().AlignMiddle().Width(7).Height(7).Element(c => SeriesFill(c, item.Series));
                    entry.AutoItem().Text(item.Label).FontSize(7.5f).FontColor(Muted);
                });
            }
        });

    /// <summary>
    /// A bar chart of the report page (<see cref="ReportPresenter"/>): the same columns, stacked segments, axis maximum,
    /// gridline labels and day labels. Heights are the page's percentages of the plot.
    /// </summary>
    private static void BarChart(IContainer container, ReportBarChart chart)
    {
        var plot = ChartHeight - TickLabelHeight;
        var longestTick = chart.Ticks.Max(t => t.Label.Length);
        var gutter = Math.Clamp(longestTick * 4.6f + 6f, 22f, 110f);
        var count = chart.Columns.Count;
        var gap = count <= 10 ? 14f : count <= 31 ? 4f : 1.5f;
        var maxBar = count <= 10 ? 26f : 14f;

        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.ConstantItem(gutter).Column(axis =>
                {
                    // Labels sit centred on the top, middle and bottom gridlines.
                    axis.Item().Height(TickLabelHeight).Element(c => Tick(c, chart.Ticks[0].Label));
                    axis.Item().Height(plot / 2 - TickLabelHeight);
                    axis.Item().Height(TickLabelHeight).Element(c => Tick(c, chart.Ticks[1].Label));
                    axis.Item().Height(plot / 2 - TickLabelHeight);
                    axis.Item().Height(TickLabelHeight).Element(c => Tick(c, chart.Ticks[2].Label));
                });

                row.RelativeItem().Height(ChartHeight).Layers(layers =>
                {
                    layers.Layer().Column(grid =>
                    {
                        grid.Item().Height(TickLabelHeight / 2);
                        grid.Item().LineHorizontal(0.5f).LineColor(Grid);
                        grid.Item().Height(plot / 2 - 0.5f);
                        grid.Item().LineHorizontal(0.5f).LineColor(Grid);
                        grid.Item().Height(plot / 2 - 1.25f);
                        grid.Item().LineHorizontal(0.75f).LineColor(BorderStrong);
                    });

                    // Every column is one equal slot (the gap is padding inside it), so day labels below can span whole columns.
                    layers.PrimaryLayer().PaddingVertical(TickLabelHeight / 2).Row(bars =>
                    {
                        foreach (var barColumn in chart.Columns)
                        {
                            bars.RelativeItem().PaddingHorizontal(gap / 2).AlignBottom().AlignCenter().MaxWidth(maxBar).ExtendHorizontal().Column(stack =>
                            {
                                if (barColumn.IsZero && chart.ShowZeroLabels)
                                    stack.Item().AlignCenter().PaddingBottom(1).Text("0").FontSize(5.5f).FontColor(Muted);

                                // The page stacks its first segment at the bottom; scaled just under the plot so rounding never overflows it.
                                foreach (var segment in barColumn.Segments.Reverse())
                                {
                                    var height = (float)(plot * 0.995 * segment.HeightPercent / 100d);
                                    if (height >= 0.3f)
                                        stack.Item().Height(height).Element(c => SeriesFill(c, segment.Series));
                                }
                            });
                        }
                    });
                });
            });

            column.Item().PaddingTop(2).Row(row =>
            {
                row.ConstantItem(gutter);
                row.RelativeItem().Row(axis =>
                {
                    foreach (var group in AxisGroups(chart.Columns))
                    {
                        var cell = axis.RelativeItem(group.Columns);
                        if (group.Label is not { } label)
                            continue;
                        var text = cell.Text(label).FontSize(6.5f).FontColor(Muted);
                        if (group.IsToday)
                            text.SemiBold();
                        _ = group.Align switch
                        {
                            AxisLabelAlign.Start => text.AlignStart(),
                            AxisLabelAlign.End => text.AlignEnd(),
                            _ => text.AlignCenter()
                        };
                    }
                });
            });
        });
    }

    private static void Tick(IContainer container, string label) =>
        container.AlignMiddle().PaddingHorizontal(3).Text(label).FontSize(6.5f).FontColor(Muted).AlignEnd();

    public enum AxisLabelAlign
    {
        Center,
        Start,
        End
    }

    public sealed record AxisLabelGroup(int Columns, string? Label, AxisLabelAlign Align, bool IsToday);

    /// <summary>
    /// The day labels under the bars, as runs of whole columns. A labelled column owns the same number of columns on both
    /// sides, up to half the distance to its neighbouring labels, so the label is centred under its bar and has room not to
    /// wrap. At the first and last column there is nothing on one side: that label starts or ends with its bar. Runs
    /// without a label fill the rest.
    /// </summary>
    public static IReadOnlyList<AxisLabelGroup> AxisGroups(IReadOnlyList<ReportChartColumn> columns)
    {
        var labelled = Enumerable.Range(0, columns.Count).Where(i => columns[i].AxisLabel is not null).ToList();
        var groups = new List<AxisLabelGroup>();
        var next = 0;
        for (var k = 0; k < labelled.Count; k++)
        {
            var i = labelled[k];
            var before = k == 0 ? int.MaxValue : i - labelled[k - 1];
            var after = k == labelled.Count - 1 ? int.MaxValue : labelled[k + 1] - i;
            var radius = (Math.Min(before, after) - 1) / 2;
            var left = Math.Max(next, i - radius);
            var right = Math.Min(columns.Count - 1, i + radius);
            var align = (left > i - radius, right < i + radius) switch
            {
                (true, false) => AxisLabelAlign.Start,
                (false, true) => AxisLabelAlign.End,
                _ => AxisLabelAlign.Center
            };

            if (left > next)
                groups.Add(new(left - next, null, AxisLabelAlign.Center, false));
            groups.Add(new(right - left + 1, columns[i].AxisLabel, align, columns[i].AxisLabelIsToday));
            next = right + 1;
        }

        if (next < columns.Count)
            groups.Add(new(columns.Count - next, null, AxisLabelAlign.Center, false));
        return groups;
    }

    private static void SeriesFill(IContainer container, string series)
    {
        if (series == ReportPresenter.SeriesPartial)
            container.Background(SeriesColor(series)).Border(0.5f).BorderColor(PartialBorder);
        else
            container.Background(SeriesColor(series));
    }

    private static Color SeriesColor(string series) => series switch
    {
        ReportPresenter.SeriesDelivered or ReportPresenter.SeriesCompletedValue => Color.FromHex("#E67140"),
        ReportPresenter.SeriesOther => Color.FromHex("#8F867B"),
        ReportPresenter.SeriesPartial => Color.FromHex("#F6CDB6"),
        "ty" => Color.FromHex("#C4700F"),
        "ys" => Color.FromHex("#B3123F"),
        "gy" => Color.FromHex("#5D3EBC"),
        _ => Muted
    };

    private static void Shares(IContainer container, ReportExportPrintContent print) =>
        container.Column(column =>
        {
            column.Spacing(5);
            column.Item().Element(c => SectionTitle(c, print.ShareTitle!, print.ShareHint));
            foreach (var share in print.Shares)
            {
                column.Item().Row(row =>
                {
                    row.Spacing(8);
                    row.ConstantItem(110).Row(name =>
                    {
                        name.Spacing(5);
                        name.AutoItem().AlignMiddle().Width(7).Height(7).Element(c => SeriesFill(c, share.Series));
                        name.RelativeItem().Text(share.Platform).SemiBold();
                    });
                    row.RelativeItem().AlignMiddle().Height(6).Background(Grid).Row(track =>
                    {
                        var filled = (float)Math.Clamp(share.BarPercent, 0d, 100d);
                        if (filled > 0f)
                            track.RelativeItem(filled).Element(c => SeriesFill(c, share.Series));
                        if (filled < 100f)
                            track.RelativeItem(100f - filled);
                    });
                    row.ConstantItem(56).Text(share.Count).SemiBold().AlignEnd();
                    row.ConstantItem(48).Text(share.Share).FontColor(Muted).AlignEnd();
                });
            }
        });

    private static void SectionTitle(IContainer container, string title, string? hint) =>
        container.Column(column =>
        {
            column.Item().Text(title).FontSize(11).Bold();
            if (hint is not null)
                column.Item().Text(hint).FontSize(7.5f).FontColor(Muted);
        });

    private static readonly float[] SummaryWidths = [2.2f, 1.3f, 6.5f];

    /// <summary>A wider first column, narrow counts, and room for the largest amounts.</summary>
    private static float[] FigureWidths(ReportExportTable table) => table.Columns
        .Select((c, i) => i == 0 ? 1.55f : c.Kind switch
        {
            ReportExportValueKind.Money => 1.3f,
            ReportExportValueKind.Rate => 1f,
            _ => 0.85f
        })
        .ToArray();

    /// <summary>A table whose header row repeats on every page it continues on.</summary>
    private static void Table(IContainer container, ReportExportTable table, float[] widths, string emptyText) =>
        container.EnsureSpace(90).Column(column =>
        {
            column.Item().PaddingBottom(4).Text(table.Title).FontSize(11).Bold();
            column.Item().Table(grid =>
            {
                grid.ColumnsDefinition(columns =>
                {
                    foreach (var width in widths)
                        columns.RelativeColumn(width);
                });

                grid.Header(header =>
                {
                    foreach (var column in table.Columns)
                    {
                        var text = header.Cell().Background(SurfaceMuted).BorderBottom(0.75f).BorderColor(BorderStrong)
                            .PaddingVertical(4).PaddingHorizontal(4)
                            .Text(column.Header).FontSize(7).Bold().FontColor(Muted);
                        if (IsNumeric(column.Kind))
                            text.AlignEnd();
                    }
                });

                foreach (var row in table.Rows)
                {
                    for (var i = 0; i < row.Count; i++)
                    {
                        var cell = row[i];
                        var text = grid.Cell().BorderBottom(0.5f).BorderColor(Border).PaddingVertical(3).PaddingHorizontal(4)
                            .Text(cell.HasValue ? cell.Display : ReportFormat.NoValue).FontSize(7.5f);
                        if (IsNumeric(cell.Kind))
                            text.AlignEnd();
                        if (i == 0)
                            text.SemiBold();
                    }
                }
            });

            if (table.Rows.Count == 0)
                column.Item().PaddingTop(4).Text(emptyText).FontColor(Muted);
        });

    private static void Notes(IContainer container, ReportExportDocument document) =>
        container.Column(column =>
        {
            column.Spacing(3);
            column.Item().PaddingBottom(2).Text(document.NotesTitle).FontSize(11).Bold();
            foreach (var note in document.Notes)
            {
                column.Item().Row(row =>
                {
                    row.Spacing(5);
                    row.ConstantItem(6).Text("•").FontColor(Muted);
                    row.RelativeItem().Text(note).FontSize(8);
                });
            }
        });

    private static bool IsNumeric(ReportExportValueKind kind) =>
        kind is ReportExportValueKind.Count or ReportExportValueKind.Money or ReportExportValueKind.Rate;
}
