using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Wasla.UnitTests.Tours;

/// <summary>
/// The guided demo and tour copy is shown in TR, EN, AR and RU. Every key must exist in each
/// culture with the same {n} placeholders, or a culture silently loses the value (for example the
/// Live Screen delivered-order duration in the success step).
/// </summary>
public sealed partial class GuidedDemoLocalizationTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];

    [Fact]
    public void DemoAndTourKeys_ExistInEveryCulture_WithMatchingPlaceholders()
    {
        var byCulture = Cultures.ToDictionary(culture => culture, Load);
        var keys = byCulture.Values
            .SelectMany(values => values.Keys)
            .Where(key => key.StartsWith("Demo.", StringComparison.Ordinal) || key.StartsWith("Tour.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var reference = Placeholders(byCulture[".en-US"][key]);
            foreach (var culture in Cultures)
            {
                Assert.True(byCulture[culture].TryGetValue(key, out var value), $"{key} missing in SharedResource{culture}.resx");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} empty in SharedResource{culture}.resx");
                Assert.Equal(reference, Placeholders(value!));
            }
        }
    }

    [Theory]
    [InlineData("Demo.SuccessBody")]
    [InlineData("Demo.SkipConfirmBody")]
    public void CopyWithAValue_TakesItFromAPlaceholder(string key)
    {
        foreach (var culture in Cultures)
            Assert.Equal(new[] { "{0}" }, Placeholders(Load(culture)[key]));
    }

    [Fact]
    public void RemovedCourierActionCopy_IsGone()
    {
        foreach (var culture in Cultures)
        {
            var values = Load(culture);
            Assert.DoesNotContain("Demo.CourierTitle", values.Keys);
            Assert.DoesNotContain("Demo.DeliveredTitle", values.Keys);
            Assert.DoesNotContain("Demo.SkipForNow", values.Keys);
        }
    }

    private static Dictionary<string, string> Load(string culture)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx");
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(
                data => (string)data.Attribute("name")!,
                data => (string?)data.Element("value") ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string[] Placeholders(string value) =>
        PlaceholderPattern().Matches(value).Select(match => match.Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
