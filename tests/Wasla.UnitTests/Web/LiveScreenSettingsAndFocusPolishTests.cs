using System.Xml.Linq;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Live Screen polish: the settings menu groups its read-only status, and Focus rows stay compact with the
/// actions reachable while a long item list scrolls.
/// </summary>
public sealed class LiveScreenSettingsAndFocusPolishTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];

    [Fact]
    public void LiveSettingsMenu_GroupsReadOnlyStatus_AndKeepsTheBootstrapDropdownContract()
    {
        var live = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("data-bs-toggle=\"dropdown\"", live);
        Assert.Contains("aria-controls=\"ordersLiveDisplaySettingsMenu\"", live);
        Assert.Contains("role=\"group\" aria-labelledby=\"ordersLiveSettingsAutomationHeading\"", live);
        Assert.Contains("role=\"group\" aria-labelledby=\"ordersLiveSettingsNotificationsHeading\"", live);
        Assert.Contains("id=\"ordersLiveSettingsAutomationHeading\">@L[\"Orders.LiveSettings.AutomationGroup\"]", live);
        Assert.Contains("id=\"ordersLiveSettingsNotificationsHeading\">@L[\"Notification.Notifications\"]", live);
        var automation = live.IndexOf("ordersLiveSettingsAutomationHeading\">", StringComparison.Ordinal);
        var notifications = live.IndexOf("ordersLiveSettingsNotificationsHeading\">", StringComparison.Ordinal);
        foreach (var id in new[] { "automationStatusSync", "automationStatusAutoApprove", "automationStatusReceipt", "automationPendingSetupHint" })
        {
            var at = live.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
            Assert.InRange(at, automation, notifications);
        }
        Assert.True(live.IndexOf("id=\"ordersLiveDisplaySoundStatus\"", StringComparison.Ordinal) > notifications);
    }

    [Fact]
    public void LiveSettingsGroupHeading_IsLocalizedInEveryResourceFile()
    {
        foreach (var culture in Cultures)
        {
            var values = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
                .Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);
            Assert.False(string.IsNullOrWhiteSpace(values["Orders.LiveSettings.AutomationGroup"]), culture);
            Assert.False(string.IsNullOrWhiteSpace(values["Notification.Notifications"]), culture);
        }
    }

    [Fact]
    public void FocusRows_ClampLongProductSummaries_ButKeepTheFullText()
    {
        var theme = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");
        var store = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-store.js");
        var summaryRule = Rule(theme, ".wasla-live-focus-entry__summary {");
        var actionsRule = Rule(theme, ".wasla-live-focus .wasla-live-detail__actions {");

        Assert.Contains("-webkit-line-clamp: 2;", summaryRule);
        Assert.Contains("overflow: hidden;", summaryRule);
        Assert.Contains("summary.title = summary.textContent;", store);
        Assert.Contains("summary.title = summaryText;", store);
        Assert.Contains("position: sticky;", actionsRule);
        Assert.Contains("inset-block-end: -1rem;", actionsRule);
        Assert.Contains("margin-block-end: -1rem;", actionsRule);
        Assert.Matches(@"\.wasla-live-focus \.wasla-live-detail__items \{\s*grid-template-columns: minmax\(0, 1fr\) auto minmax\(4\.5rem, auto\);", theme);
    }

    // Browser review: the pending value wrapped into an oval pill and squeezed its label.
    [Fact]
    public void SettingsMenuValues_UseAModestRadius_SoTwoLineValuesStayReadable()
    {
        var theme = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");
        var value = Rule(theme, ".wasla-live-screen-page .wasla-live-settings-menu__value {");

        Assert.Contains("border-radius: .6rem;", value);
        Assert.Contains("max-width: min(20rem, calc(100vw - 1.5rem));", Rule(theme, ".wasla-live-screen-page .wasla-live-settings-menu {"));
    }

    private static string Rule(string css, string selectorWithBrace)
    {
        var start = css.IndexOf(selectorWithBrace, StringComparison.Ordinal);
        Assert.True(start >= 0, selectorWithBrace);
        return css[start..css.IndexOf('}', start)];
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(segments).ToArray()));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
