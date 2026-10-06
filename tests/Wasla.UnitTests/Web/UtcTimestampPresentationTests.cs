using System.Globalization;
using Wasla.Web.Ui;

namespace Wasla.UnitTests.Web;

public sealed class UtcTimestampPresentationTests
{
    // How SQL Server datetime2 values arrive: the stored UTC value with DateTimeKind.Unspecified.
    private static readonly DateTime Stored = new(2026, 10, 6, 9, 15, 30, DateTimeKind.Unspecified);
    private static readonly DateTime StoredUtc = DateTime.SpecifyKind(Stored, DateTimeKind.Utc);

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("ru-RU")]
    public void Display_UsesTheCultureFormat_KeepsTheUtcClockTime_AndMarksUtc(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        var text = UtcTimestampPresentation.ToDisplay(Stored, culture);

        Assert.Equal(StoredUtc.ToString("g", culture) + " UTC", text);
        Assert.Contains(StoredUtc.ToString("t", culture), text, StringComparison.Ordinal);
        Assert.EndsWith(" UTC", text, StringComparison.Ordinal);
    }

    // Exact short patterns differ between ICU versions (for example "6.10.2026" or "06.10.2026"), so these
    // check the culture's day/month order and the unshifted UTC clock time rather than one pattern.
    [Theory]
    [InlineData("tr-TR", "6.10.2026", "09:15")]
    [InlineData("en-US", "10/6/2026", "9:15")]
    [InlineData("ru-RU", "6.10.2026", "9:15")]
    public void Display_ShowsTheCultureDateOrderAndTheUtcClockTime(string cultureName, string date, string time)
    {
        var text = UtcTimestampPresentation.ToDisplay(Stored, CultureInfo.GetCultureInfo(cultureName));

        Assert.Contains(date, text, StringComparison.Ordinal);
        Assert.Contains(time, text, StringComparison.Ordinal);
        Assert.EndsWith(" UTC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_InArabic_UsesTheArabicCultureCalendar_LikeTheRestOfTheTenantUsersPage()
    {
        var arabic = CultureInfo.GetCultureInfo("ar-SA");

        var text = UtcTimestampPresentation.ToDisplay(Stored, arabic);

        // ar-SA formats with its default calendar, the same as the neighbouring "Created" column.
        Assert.StartsWith(StoredUtc.ToString("d", arabic), text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_DefaultsToTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            var turkish = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;

            Assert.Equal(UtcTimestampPresentation.ToDisplay(Stored, turkish), UtcTimestampPresentation.ToDisplay(Stored));
            Assert.Contains("6.10.2026 09:15", UtcTimestampPresentation.ToDisplay(Stored), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Fact]
    public void LocalKindValue_IsConvertedToUtc_NeverShownAsServerLocalTime()
    {
        var local = StoredUtc.ToLocalTime();
        var culture = CultureInfo.GetCultureInfo("en-US");

        Assert.Equal(UtcTimestampPresentation.ToDisplay(StoredUtc, culture), UtcTimestampPresentation.ToDisplay(local, culture));
        Assert.Equal("2026-10-06T09:15:30.0000000Z", UtcTimestampPresentation.ToIso(local));
    }

    [Fact]
    public void Iso_IsInvariantRoundTripUtc_ForTimeDatetimeAndSorting()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            Assert.Equal("2026-10-06T09:15:30.0000000Z", UtcTimestampPresentation.ToIso(Stored));
            Assert.Equal("2026-10-06T09:15:30.0000000Z", UtcTimestampPresentation.ToIso(StoredUtc));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
