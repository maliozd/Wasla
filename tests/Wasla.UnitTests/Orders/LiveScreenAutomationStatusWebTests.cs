using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// The pending automation copy and its presentation: every culture has it, the Turkish is the agreed text, nothing is
/// hard-coded in Razor or JavaScript, and the pending look uses logical CSS so it reads the same in RTL.
/// </summary>
public sealed partial class LiveScreenAutomationStatusWebTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];
    private static readonly string[] Keys = ["Orders.Automation.PendingSetup", "Orders.Automation.PendingSetupDescription"];

    [Fact]
    public void PendingCopy_IsTheAgreedTurkish_AndEveryCultureHasItOnce_WithoutPlaceholders()
    {
        foreach (var turkish in new[] { "", ".tr-TR" })
        {
            var values = Load(turkish);
            Assert.Equal("Kurulum tamamlanınca aktif", values["Orders.Automation.PendingSetup"]);
            Assert.Equal("Eğitimi tamamladığınızda veya atladığınızda otomatik olarak etkinleşir.", values["Orders.Automation.PendingSetupDescription"]);
        }

        foreach (var culture in Cultures)
        {
            var names = Names(culture);
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
            var values = Load(culture);
            foreach (var key in Keys)
            {
                Assert.Equal(1, names.Count(name => name == key));
                Assert.False(string.IsNullOrWhiteSpace(values[key]), $"{key} is empty in SharedResource{culture}.resx");
                Assert.DoesNotMatch(@"\{\d+\}", values[key]);
            }

            // The short state is not the same words as Active or Disabled (the neutral file is a Turkish subset).
            var cultureWithLabels = values.ContainsKey("Common.Active") ? values : Load(".tr-TR");
            Assert.NotEqual(cultureWithLabels["Common.Active"], values["Orders.Automation.PendingSetup"]);
            Assert.NotEqual(cultureWithLabels["Notification.Disabled"], values["Orders.Automation.PendingSetup"]);
        }

        foreach (var culture in new[] { ".en-US", ".ar-SA", ".ru-RU" })
            foreach (var key in Keys)
                Assert.NotEqual(Load(".tr-TR")[key], Load(culture)[key]);
    }

    [Fact]
    public void PendingCopy_ComesFromResources_InTheLiveScreen_AndTheSettingsPages()
    {
        var live = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        Assert.Contains("[\"automationStatusPendingSetup\"] = L[\"Orders.Automation.PendingSetup\"].Value", live, StringComparison.Ordinal);
        Assert.Matches("<p id=\"automationPendingSetupHint\" class=\"wasla-live-settings-menu__hint\" hidden>@L\\[\"Orders.Automation.PendingSetupDescription\"\\]</p>", live);

        foreach (var (view, id) in new[]
                 {
                     (Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "OrderSettings", "Index.cshtml"), "autoApprovePendingSetup"),
                     (Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "ReceiptPrinterSettings", "Index.cshtml"), "receiptAutomationPendingSetup")
                 })
        {
            var note = Regex.Match(view, $"<p id=\"{id}\"[^>]*>[\\s\\S]*?</p>").Value;
            Assert.Contains("role=\"note\"", note, StringComparison.Ordinal);
            Assert.Contains(" hidden>", note, StringComparison.Ordinal);
            Assert.Contains("@L[\"Orders.Automation.PendingSetup\"]", note, StringComparison.Ordinal);
            Assert.Contains("@L[\"Orders.Automation.PendingSetupDescription\"]", note, StringComparison.Ordinal);
            // The saved toggle stays a normal, enabled control: the note only explains.
            Assert.DoesNotContain("disabled", note, StringComparison.Ordinal);
        }

        var turkish = Load(".tr-TR");
        foreach (var file in new[]
                 {
                     live,
                     Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "OrderSettings", "Index.cshtml"),
                     Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "ReceiptPrinterSettings", "Index.cshtml"),
                     Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-automation-status.js"),
                     Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-order-settings.js")
                 })
        {
            foreach (var key in Keys)
                Assert.DoesNotContain(turkish[key], file, StringComparison.Ordinal);
            foreach (var english in new[] { "Active once setup", "after setup", "Turns on automatically" })
                Assert.DoesNotContain(english, file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PendingLook_UsesLogicalProperties_SoItReadsTheSameRightToLeft()
    {
        var css = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");
        var rules = RuleBlocks().Matches(css)
            .Where(match => match.Groups[1].Value.Contains("wasla-orders-status-chip--pending", StringComparison.Ordinal)
                || match.Groups[1].Value.Contains("wasla-live-settings-menu__hint", StringComparison.Ordinal))
            .Select(match => match.Groups[2].Value)
            .ToList();

        Assert.True(rules.Count >= 4, "light and dark rules for the pending chip and the explanation line");
        foreach (var body in rules)
            foreach (var physical in new[] { "left", "right", "float", "position:" })
                Assert.DoesNotContain(physical, body, StringComparison.Ordinal);
        Assert.Contains(rules, body => body.Contains("text-align: end", StringComparison.Ordinal));
        Assert.Contains(rules, body => body.Contains("padding-inline", StringComparison.Ordinal));
    }

    private static List<string> Names(string culture) =>
        XDocument.Load(Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
            .Root!
            .Elements("data")
            .Select(element => element.Attribute("name")?.Value ?? string.Empty)
            .ToList();

    private static Dictionary<string, string> Load(string culture) =>
        XDocument.Load(Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

    private static string Read(params string[] segments) => File.ReadAllText(Path.Combine([Root(), .. segments]));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex RuleBlocks();
}
