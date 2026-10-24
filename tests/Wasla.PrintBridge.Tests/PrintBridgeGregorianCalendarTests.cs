using System.Globalization;
using System.Text.Json;
using Wasla.Application.Printing;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Tests.WebShell;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// Operational times (orders, print jobs, connection) are always Gregorian, also in Arabic. ar-SA defaults to the
/// Umm al-Qura calendar, which would print 1448 instead of 2026 and break comparison with provider and server records.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class PrintBridgeGregorianCalendarTests : IDisposable
{
    private static readonly DateTime LocalTime = new(2026, 10, 4, 20, 23, 0, DateTimeKind.Local);

    private readonly CultureScope _cultureScope = new();

    public void Dispose() => _cultureScope.Dispose();

    [Fact]
    public void ArabicDefaultsToANonGregorianCalendar_WhichIsWhyTheGuardExists()
    {
        var arabic = CultureInfo.GetCultureInfo(SupportedCultures.Arabic);

        Assert.IsNotType<GregorianCalendar>(arabic.DateTimeFormat.Calendar);
        Assert.NotEqual("2026", LocalTime.ToString("yyyy", arabic));
    }

    [Theory]
    [InlineData(SupportedCultures.Turkish)]
    [InlineData(SupportedCultures.English)]
    [InlineData(SupportedCultures.Arabic)]
    [InlineData(SupportedCultures.Russian)]
    public void GregorianCulture_KeepsTheLanguageButUsesTheGregorianCalendar(string name)
    {
        var source = CultureInfo.GetCultureInfo(name);

        var gregorian = GregorianCulture.For(source);

        Assert.Equal(name, gregorian.Name);
        Assert.IsType<GregorianCalendar>(gregorian.DateTimeFormat.Calendar);
        Assert.True(gregorian.IsReadOnly);
        Assert.Equal("2026", LocalTime.ToString("yyyy", gregorian));
        Assert.Same(gregorian, GregorianCulture.For(source));
    }

    [Fact]
    public void GregorianCulture_ReturnsAlreadyGregorianCulturesUnchanged()
    {
        var turkish = CultureInfo.GetCultureInfo(SupportedCultures.Turkish);

        Assert.Same(turkish, GregorianCulture.For(turkish));
    }

    [Fact]
    public void GregorianArabic_StillUsesArabicNamesAndSeparators()
    {
        var gregorian = GregorianCulture.For(CultureInfo.GetCultureInfo(SupportedCultures.Arabic));

        Assert.DoesNotContain("October", LocalTime.ToString("MMMM", gregorian), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CultureInfo.GetCultureInfo(SupportedCultures.Arabic).NumberFormat.NumberDecimalSeparator, gregorian.NumberFormat.NumberDecimalSeparator);
        Assert.Equal(CultureInfo.GetCultureInfo(SupportedCultures.Arabic).TextInfo.IsRightToLeft, gregorian.TextInfo.IsRightToLeft);
    }

    [Theory]
    [InlineData(SupportedCultures.Turkish, "04.10.2026 20:23", "04.10.2026 20:23:00")]
    [InlineData(SupportedCultures.English, "2026-10-04 20:23", "2026-10-04 20:23:00")]
    [InlineData(SupportedCultures.Arabic, "04/10/2026 20:23", "04/10/2026 20:23:00")]
    [InlineData(SupportedCultures.Russian, "04.10.2026 20:23", "04.10.2026 20:23:00")]
    public void DesktopTimestamps_AreGregorianInEveryLanguage(string name, string dashboard, string tooltip)
    {
        var culture = CultureInfo.GetCultureInfo(name);

        Assert.Equal(dashboard, Plain(PrintBridgeDateTimeFormatter.FormatDashboard(culture, LocalTime)));
        Assert.Equal(tooltip, Plain(PrintBridgeDateTimeFormatter.FormatTooltip(culture, LocalTime)));
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData("ru")]
    [InlineData(null)]
    public void ReceiptCulture_IsGregorianForEveryReceiptLanguage(string? language)
    {
        var culture = ReceiptLabelLocalizer.GetCulture(language);

        Assert.IsType<GregorianCalendar>(culture.DateTimeFormat.Calendar);
        Assert.Equal("2026", LocalTime.ToString("yyyy", culture));
    }

    [Fact]
    public void ArabicReceipt_PrintsTheGregorianOrderDate()
    {
        // Noon UTC stays on 4 October in every time zone the test machine may use.
        var receivedAtUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var payload = JsonSerializer.Serialize(new
        {
            ExternalOrderCode = "GTR-1001",
            ReceivedAtUtc = receivedAtUtc,
            Template = new { Language = "ar", ShowReceivedTime = true }
        });

        var receipt = Plain(new ReceiptFormatter().Format(payload, normalizeTurkishChars: false));

        var gregorianDate = Plain(receivedAtUtc.ToLocalTime().ToString("d", ReceiptLabelLocalizer.GetCulture("ar")));
        var hijriDate = Plain(receivedAtUtc.ToLocalTime().ToString("d", CultureInfo.GetCultureInfo(SupportedCultures.Arabic)));
        Assert.StartsWith("4/10/2026", gregorianDate, StringComparison.Ordinal);
        Assert.Contains(gregorianDate, receipt, StringComparison.Ordinal);
        Assert.DoesNotContain(hijriDate, receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("1448", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void ArabicShellSnapshot_ShowsGregorianActivityAndDiagnosticsTimes()
    {
        var contactUtc = LocalTime.ToUniversalTime();
        using var rig = new ShellTestRig(Culture(SupportedCultures.Arabic));
        var job = new LocalPrintJobRecord
        {
            JobId = Guid.NewGuid(),
            OrderDisplay = "GTR-1001",
            Status = LocalPrintJobStatus.Printed,
            CreatedAtUtc = contactUtc,
            PrintedAtUtc = contactUtc
        };

        var snapshot = rig.Factory.Create(Status(lastContactUtc: contactUtc, lastPrintUtc: contactUtc, jobs: [job]));

        Assert.Equal("04/10/2026 20:23", Plain(snapshot.Activity.LastContact));
        Assert.Equal("04/10/2026 20:23", Plain(snapshot.Activity.LastPrint));
        Assert.Equal("04/10/2026 20:23", Plain(snapshot.LastJob!.Time));
        Assert.Equal("04/10/2026 20:23", Plain(snapshot.Diagnostics.LastContact));
    }

    [Fact]
    public void ArabicHistoryRows_UseGregorianTimes()
    {
        using var rig = new ShellTestRig(Culture(SupportedCultures.Arabic));
        rig.Engine.History =
        [
            new LocalPrintJobRecord
            {
                JobId = Guid.NewGuid(),
                OrderDisplay = "GTR-1001",
                Status = LocalPrintJobStatus.Printed,
                CreatedAtUtc = LocalTime.ToUniversalTime(),
                PrintedAtUtc = LocalTime.ToUniversalTime()
            }
        ];

        var page = rig.History.Query(PrintHistoryDateFilter.Today, 0, null);

        Assert.Equal("04/10/2026 20:23", Plain(Assert.Single(page.Items).Time));
    }

    /// <summary>Arabic date separators carry right-to-left marks; compare the visible characters only.</summary>
    private static string Plain(string? value) =>
        (value ?? string.Empty).Replace("‏", string.Empty, StringComparison.Ordinal).Replace("‎", string.Empty, StringComparison.Ordinal);
}
