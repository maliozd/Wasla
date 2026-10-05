using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml.Linq;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.UI;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-58: how both windows show a connection change that was refused because a print job is still being completed,
/// and the text they show in every culture.
/// </summary>
public sealed partial class PrintBridgeSetupLinkRefusalUiTests
{
    private static readonly string[] RefusalKeys = ["Auto.PrintingInProgress", "Settings.Connection.PrintingInProgress"];

    private static readonly (string File, string Culture)[] ResourceFiles =
    [
        ("PrintBridgeResources.resx", "neutral"),
        ("PrintBridgeResources.tr-TR.resx", "tr"),
        ("PrintBridgeResources.en-US.resx", "en"),
        ("PrintBridgeResources.ar-SA.resx", "ar"),
        ("PrintBridgeResources.ru-RU.resx", "ru")
    ];

    [Fact]
    public void ARefusedSetupLink_IsShownAsNotApplied_InBothWindows_AndNothingIsReloadedOrVerified()
    {
        var view = TrayApplicationContext.DescribeAutoSetupOutcome(PrintBridgeAutoSetupOutcome.PrintingInProgress);

        Assert.Equal("Auto.PrintingInProgress", view.MessageKey);
        Assert.Equal(MessageBoxIcon.Warning, view.Icon);
        Assert.False(view.Saved);
        Assert.False(view.Connected);
    }

    [Theory]
    [InlineData(PrintBridgeAutoSetupOutcome.Connected, "Auto.Connected", true, true)]
    [InlineData(PrintBridgeAutoSetupOutcome.ConnectedPrinterMissing, "Auto.ConnectedPrinterMissing", true, true)]
    [InlineData(PrintBridgeAutoSetupOutcome.SavedButUnverified, "Auto.SavedUnverified", true, false)]
    [InlineData(PrintBridgeAutoSetupOutcome.InvalidOrExpired, "Auto.InvalidOrExpired", false, false)]
    [InlineData(PrintBridgeAutoSetupOutcome.Failed, "Auto.Failed", false, false)]
    public void TheOtherSetupLinkOutcomes_AreShownAsBefore(PrintBridgeAutoSetupOutcome outcome, string key, bool saved, bool connected)
    {
        var view = TrayApplicationContext.DescribeAutoSetupOutcome(outcome);

        Assert.Equal((key, saved, connected), (view.MessageKey, view.Saved, view.Connected));
        Assert.Equal(connected ? MessageBoxIcon.Information : MessageBoxIcon.Warning, view.Icon);
    }

    [Fact]
    public void ClassicSaveAndTest_AskTheEngineBeforeChangingTheConnection()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Wasla.PrintBridge", "UI", "MainForm.SettingsSave.cs"));

        foreach (var method in new[] { "SaveConnectionSettingsAsync", "TestConnectionFromSettingsAsync" })
        {
            var start = source.IndexOf($"private async Task {method}()", StringComparison.Ordinal);
            var end = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
            var body = source[start..end];
            var guard = body.IndexOf("TryBeginConnectionChange()", StringComparison.Ordinal);

            Assert.True(guard > 0, $"{method} does not ask the engine first.");
            Assert.True(guard < body.IndexOf("_settingsHolder.Replace(", StringComparison.Ordinal), $"{method} changes the connection before asking.");
            Assert.True(guard < body.IndexOf("_runtime.StopAsync()", StringComparison.Ordinal) || !body.Contains("_runtime.StopAsync()", StringComparison.Ordinal));
        }

        Assert.Contains("_localizer[\"Settings.Connection.PrintingInProgress\"]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalText_IsTranslatedInEveryCulture_AndContainsNoAddressOrSecret()
    {
        var turkish = Load("PrintBridgeResources.tr-TR.resx");

        foreach (var (file, culture) in ResourceFiles)
        {
            var values = Load(file);
            foreach (var key in RefusalKeys)
            {
                Assert.True(values.TryGetValue(key, out var value), $"{file} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: {key} is empty.");
                Assert.DoesNotMatch(SecretLikeText(), value);

                if (culture is "en" or "ar" or "ru")
                    Assert.NotEqual(turkish[key], value);
                if (culture == "ar")
                    Assert.Matches(ArabicLetters(), value);
                if (culture == "ru")
                    Assert.Matches(CyrillicLetters(), value);
            }
        }

        Assert.Equal(turkish["Auto.PrintingInProgress"], Load("PrintBridgeResources.resx")["Auto.PrintingInProgress"]);
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Wasla.PrintBridge", "Resources", file)).Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty, StringComparer.Ordinal);

    [GeneratedRegex(@"https?://|\{\d|token|wasla-printbridge:", RegexOptions.IgnoreCase)]
    private static partial Regex SecretLikeText();

    [GeneratedRegex(@"\p{IsArabic}")]
    private static partial Regex ArabicLetters();

    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex CyrillicLetters();
}
