using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

public sealed partial class ShellLocalizationTests
{
    private static readonly string[] ResourceFiles =
    [
        "PrintBridgeResources.resx",
        "PrintBridgeResources.tr-TR.resx",
        "PrintBridgeResources.en-US.resx",
        "PrintBridgeResources.ar-SA.resx",
        "PrintBridgeResources.ru-RU.resx"
    ];

    /// <summary>The WebView2 Runtime recovery notice the tray shows when the app window cannot open.</summary>
    private static readonly string[] RuntimeNoticeKeys =
    [
        "RuntimeNotice.Title",
        "RuntimeNotice.Message",
        "RuntimeNotice.Close"
    ];

    [Fact]
    public void ResourceFiles_AreValidXml_WithoutDuplicateKeys()
    {
        foreach (var file in ResourceFiles)
        {
            var keys = LoadKeys(file).Select(k => k.Key).ToList();

            Assert.NotEmpty(keys);
            Assert.Empty(keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{file}: {g.Key}"));
        }
    }

    [Fact]
    public void AllCultures_DefineTheSameKeys()
    {
        var neutral = LoadKeys(ResourceFiles[0]).Select(k => k.Key).Order(StringComparer.Ordinal).ToArray();

        foreach (var file in ResourceFiles.Skip(1))
            Assert.Equal(neutral, LoadKeys(file).Select(k => k.Key).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Placeholders_AreConsistentAcrossCultures()
    {
        var neutral = LoadKeys(ResourceFiles[0]).ToDictionary(k => k.Key, k => Placeholders(k.Value), StringComparer.Ordinal);

        foreach (var file in ResourceFiles.Skip(1))
        {
            foreach (var (key, value) in LoadKeys(file))
                Assert.True(neutral[key] == Placeholders(value), $"{file}: {key} has placeholders '{Placeholders(value)}', neutral has '{neutral[key]}'.");
        }
    }

    [Fact]
    public void EveryShellKey_HasANonEmptyTranslationInEveryCulture()
    {
        var shellKeys = ShellSnapshotFactory.StringKeys.Concat(ShellSnapshotFactory.StateKeys).Concat(RuntimeNoticeKeys).Distinct().ToArray();

        foreach (var file in ResourceFiles)
        {
            var values = LoadKeys(file).ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
            foreach (var key in shellKeys)
            {
                Assert.True(values.TryGetValue(key, out var value), $"{file} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: {key} is empty.");
            }
        }
    }

    [Fact]
    public void NewShellStrings_AreTranslated_NotCopiedFromTurkish()
    {
        var turkish = LoadKeys("PrintBridgeResources.tr-TR.resx").ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var neutral = LoadKeys("PrintBridgeResources.resx").ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var shellKeys = turkish.Keys.Where(k => k.StartsWith("Shell.", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(shellKeys);
        foreach (var key in shellKeys)
        {
            Assert.Equal(turkish[key], neutral[key]);
            foreach (var file in new[] { "PrintBridgeResources.en-US.resx", "PrintBridgeResources.ar-SA.resx", "PrintBridgeResources.ru-RU.resx" })
                Assert.NotEqual(turkish[key], LoadKeys(file).Single(k => k.Key == key).Value);
        }
    }

    [Fact]
    public void RuntimeNotice_IsTranslatedInEveryCulture_NamesTheRuntime_AndSaysPrintingContinues()
    {
        var turkish = LoadKeys("PrintBridgeResources.tr-TR.resx").ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var neutral = LoadKeys("PrintBridgeResources.resx").ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var english = LoadKeys("PrintBridgeResources.en-US.resx").ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var arabic = LoadKeys("PrintBridgeResources.ar-SA.resx").ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var russian = LoadKeys("PrintBridgeResources.ru-RU.resx").ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);

        foreach (var key in RuntimeNoticeKeys)
        {
            Assert.Equal(turkish[key], neutral[key]);
            Assert.All(new[] { english, arabic, russian }, other => Assert.NotEqual(turkish[key], other[key]));
            Assert.Empty(PlaceholderRegex().Matches(turkish[key]));
        }

        foreach (var values in new[] { turkish, english, arabic, russian })
            Assert.Contains("Microsoft Edge WebView2 Runtime", values["RuntimeNotice.Message"], StringComparison.Ordinal);

        Assert.Equal("WebView2 Runtime required", english["RuntimeNotice.Title"]);
        Assert.Contains("Receipt printing continues", english["RuntimeNotice.Message"], StringComparison.Ordinal);
        Assert.Matches(@"\p{IsArabic}", arabic["RuntimeNotice.Message"]);
        Assert.Matches(@"\p{IsArabic}", arabic["RuntimeNotice.Close"]);
        Assert.Matches(@"\p{IsCyrillic}", russian["RuntimeNotice.Message"]);
        Assert.Matches(@"\p{IsCyrillic}", russian["RuntimeNotice.Close"]);
    }

    [Fact]
    public void PageLabels_UseOnlyKeysTheHostSends()
    {
        var html = File.ReadAllText(Path.Combine(AssetsDirectory(), "index.html"));
        var scripts = File.ReadAllText(Path.Combine(AssetsDirectory(), "shell.js"))
                      + File.ReadAllText(Path.Combine(AssetsDirectory(), "shell-model.js"));
        var pageKeys = DataI18nRegex().Matches(html).Select(m => m.Groups[1].Value)
            .Concat(ResourceKeyLiteralRegex().Matches(scripts).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(pageKeys);
        Assert.All(pageKeys, key => Assert.Contains(key, ShellSnapshotFactory.StringKeys));
        Assert.All(ShellSnapshotFactory.StringKeys.Where(k => k != "Common.Dash"), key => Assert.Contains(key, pageKeys));
    }

    [Fact]
    public void Page_ContainsNoHardcodedUserText()
    {
        var html = File.ReadAllText(Path.Combine(AssetsDirectory(), "index.html"));
        var body = html[html.IndexOf("<body>", StringComparison.Ordinal)..];

        // Every text node in the body is empty: all visible text comes from host snapshots. The only literal
        // is the toast close glyph, which has a localized accessible name. The brand is the logo image.
        var visibleText = TextNodeRegex().Matches(body).Select(m => m.Groups[1].Value.Trim()).Where(t => t.Length > 0).ToArray();
        Assert.Equal(["×"], visibleText);
    }

    private static IEnumerable<KeyValuePair<string, string>> LoadKeys(string file)
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "Wasla.PrintBridge", "Resources", file);
        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(d => new KeyValuePair<string, string>((string)d.Attribute("name")!, (string?)d.Element("value") ?? string.Empty));
    }

    private static string Placeholders(string value) =>
        string.Join(",", PlaceholderRegex().Matches(value).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));

    [GeneratedRegex(@"\{\d+(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex("data-i18n(?:-label|-placeholder)?=\"([^\"]+)\"")]
    private static partial Regex DataI18nRegex();

    /// <summary>Resource keys referenced from script, e.g. <c>'Button.StartListening'</c> (no spaces, dotted).</summary>
    [GeneratedRegex(@"'([A-Z][A-Za-z]+(?:\.[A-Za-z]+)+)'")]
    private static partial Regex ResourceKeyLiteralRegex();

    [GeneratedRegex(@">([^<]*)<")]
    private static partial Regex TextNodeRegex();
}
