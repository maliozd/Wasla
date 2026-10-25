using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Parity of the Web resources as a whole. The four culture files carry the same keys, each once and non-empty, with the
/// same format placeholders. The neutral file is a Turkish subset: every key it has exists in the cultures with the same
/// placeholders. Russian on/off status labels keep the meaning of the English ones.
/// </summary>
public sealed partial class SharedResourceParityTests
{
    private static readonly string[] Cultures = [".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];

    [Fact]
    public void CultureFiles_HaveTheSameKeys_EachOnceAndNonEmpty()
    {
        var english = Names(".en-US").ToHashSet(StringComparer.Ordinal);
        foreach (var culture in Cultures)
        {
            var names = Names(culture);
            var duplicates = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicates.Count == 0, $"Duplicate keys in SharedResource{culture}.resx: {string.Join(", ", duplicates)}");

            var missing = english.Except(names, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var extra = names.Except(english, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, $"Missing in SharedResource{culture}.resx: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"Only in SharedResource{culture}.resx: {string.Join(", ", extra)}");

            var empty = Load(culture).Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToList();
            Assert.True(empty.Count == 0, $"Empty values in SharedResource{culture}.resx: {string.Join(", ", empty)}");
        }
    }

    [Fact]
    public void NeutralFile_IsATurkishSubsetOfTheCultures_WithoutDuplicates()
    {
        var names = Names("");
        var duplicates = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, $"Duplicate keys in SharedResource.resx: {string.Join(", ", duplicates)}");

        foreach (var culture in Cultures)
        {
            var missing = names.Except(Names(culture), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, $"Neutral keys missing in SharedResource{culture}.resx: {string.Join(", ", missing)}");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".tr-TR")]
    [InlineData(".en-US")]
    [InlineData(".ar-SA")]
    [InlineData(".ru-RU")]
    public void ReceiptRequiredFieldsDescription_ExistsInEveryResourceFile(string culture)
    {
        var values = Load(culture);
        Assert.True(values.TryGetValue("Settings.ReceiptContent.RequiredFieldsDescription", out var value), $"Missing in SharedResource{culture}.resx");
        Assert.False(string.IsNullOrWhiteSpace(value));
    }

    [Fact]
    public void Placeholders_MatchEnglishInEveryFile()
    {
        var english = Load(".en-US");
        var mismatches = new List<string>();
        foreach (var culture in new[] { "", ".tr-TR", ".ar-SA", ".ru-RU" })
            foreach (var (key, value) in Load(culture))
                if (english.TryGetValue(key, out var reference) && Placeholders(reference) != Placeholders(value))
                    mismatches.Add($"SharedResource{culture}.resx {key}: '{Placeholders(value)}', English '{Placeholders(reference)}'");

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void RussianOnOffLabels_KeepTheEnglishMeaning()
    {
        var english = Load(".en-US");
        var russian = Load(".ru-RU");
        var off = english.Where(p => OffLabel().IsMatch(p.Value)).Select(p => p.Key).ToList();
        var on = english.Where(p => OnLabel().IsMatch(p.Value)).Select(p => p.Key).ToList();
        Assert.Contains("Notification.Disabled", off);
        Assert.Contains("Notification.Enabled", on);

        foreach (var key in off)
        {
            Assert.Matches(RussianOff(), russian[key]);
            Assert.DoesNotMatch(RussianDisabilitySense(), russian[key]);
        }

        foreach (var key in on)
            Assert.DoesNotMatch(RussianOff(), russian[key]);

        // The notification switch reads as a pair: on / off.
        Assert.Equal("Включено", russian["Notification.Enabled"]);
        Assert.Equal("Выключено", russian["Notification.Disabled"]);
    }

    private static List<string> Names(string culture) =>
        Document(culture).Root!.Elements("data").Select(e => e.Attribute("name")?.Value ?? string.Empty).ToList();

    private static Dictionary<string, string> Load(string culture)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in Document(culture).Root!.Elements("data"))
            values.TryAdd(element.Attribute("name")!.Value, element.Element("value")?.Value ?? string.Empty);
        return values;
    }

    private static XDocument Document(string culture) =>
        XDocument.Load(Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"));

    /// <summary>Format items by index (<c>{0}</c>, <c>{0:N2}</c> and <c>{0,5}</c> count as <c>{0}</c>), sorted.</summary>
    private static string Placeholders(string value) =>
        string.Join(",", PlaceholderPattern().Matches(value).Select(m => "{" + m.Groups[1].Value + "}").Order(StringComparer.Ordinal));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Wasla.sln was not found.");
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^(Disabled|Off|Inactive)$")]
    private static partial Regex OffLabel();

    [GeneratedRegex(@"^(Enabled|On|Active)$")]
    private static partial Regex OnLabel();

    [GeneratedRegex(@"^(Выкл|Откл|Неактив)", RegexOptions.IgnoreCase)]
    private static partial Regex RussianOff();

    [GeneratedRegex(@"неполноцен|инвалид|недееспособ|ущербн", RegexOptions.IgnoreCase)]
    private static partial Regex RussianDisabilitySense();
}
