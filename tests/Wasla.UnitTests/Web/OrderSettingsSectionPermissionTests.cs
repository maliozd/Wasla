namespace Wasla.UnitTests.Web;

public sealed class OrderSettingsSectionPermissionTests
{
    [Fact]
    public void OrderSettingsController_AllowsNotificationManagers_NotOnlyAutomationManagers()
    {
        var source = ReadWebFile("Areas", "Tenant", "Controllers", "OrderSettingsController.cs");

        Assert.Contains("CanManageOrderNotifications", source, StringComparison.Ordinal);
        Assert.Contains("CanManageOrderAutomation", source, StringComparison.Ordinal);
        Assert.Contains("OrderSettingsPageViewModel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Policy = TenantPolicies.TenantManagerOrOwner", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderSettingsView_HidesAutomationSections_UnlessPermitted()
    {
        var source = ReadWebFile("Areas", "Tenant", "Views", "OrderSettings", "Index.cshtml");

        Assert.Contains("Model.CanManageOrderAutomation", source, StringComparison.Ordinal);
        Assert.Contains("Model.CanManageOrderNotifications", source, StringComparison.Ordinal);
        Assert.Contains("id=\"notifications\"", source, StringComparison.Ordinal);
        Assert.Contains("Settings.OrderSynchronization.Title", source, StringComparison.Ordinal);
        Assert.Contains("Settings.AutoApproval.Title", source, StringComparison.Ordinal);
        Assert.Contains("Settings.Notifications.Title", source, StringComparison.Ordinal);

        // Automation sections and notification section are gated separately.
        Assert.Contains("@if (Model.CanManageOrderAutomation)", source, StringComparison.Ordinal);
        Assert.Contains("@if (Model.CanManageOrderNotifications)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersSyncAndAutoApproveEndpoints_RequireAutomationPolicy()
    {
        var source = ReadWebFile("Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("CanManageOrderAutomation", source, StringComparison.Ordinal);
        Assert.Contains("HttpPost(\"sync-settings\")", source, StringComparison.Ordinal);
        Assert.Contains("HttpPost(\"order-settings\")", source, StringComparison.Ordinal);

        var syncPostIdx = source.IndexOf("[HttpPost(\"sync-settings\")]", StringComparison.Ordinal);
        var orderPostIdx = source.IndexOf("[HttpPost(\"order-settings\")]", StringComparison.Ordinal);
        Assert.True(syncPostIdx > 0);
        Assert.True(orderPostIdx > 0);

        var syncWindow = source.Substring(syncPostIdx, Math.Min(220, source.Length - syncPostIdx));
        var orderWindow = source.Substring(orderPostIdx, Math.Min(220, source.Length - orderPostIdx));
        Assert.Contains("CanManageOrderAutomation", syncWindow, StringComparison.Ordinal);
        Assert.Contains("CanManageOrderAutomation", orderWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationSettingsController_RequiresNotificationPolicy()
    {
        var source = ReadWebFile("Areas", "Tenant", "Controllers", "NotificationSettingsController.cs");

        Assert.Contains("CanManageOrderNotifications", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersToolbar_ShowsSettingsLinkForNotificationManagers_HidesAutomationChipsWithoutManageSettings()
    {
        var source = ReadWebFile("Areas", "Tenant", "Views", "Orders", "Index.cshtml");

        Assert.Contains("CanManageOrderSettings", source, StringComparison.Ordinal);
        Assert.Contains("CanViewOrderSettingsPage", source, StringComparison.Ordinal);
        Assert.Contains("CanManageOrderNotifications", source, StringComparison.Ordinal);
        Assert.Contains("/settings/orders#notifications", source, StringComparison.Ordinal);
        Assert.Contains("ordersSoundControl", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">…</span>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_RegistersOrderSettingsSectionPolicies()
    {
        var source = ReadWebFile("Program.cs");

        Assert.Contains("CanManageOrderAutomation", source, StringComparison.Ordinal);
        Assert.Contains("CanManageOrderNotifications", source, StringComparison.Ordinal);
        Assert.Contains("UserRole.Kitchen, UserRole.Cashier", source, StringComparison.Ordinal);
    }

    private static string ReadWebFile(params string[] relativeParts)
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(new[] { root, "src", "Wasla.Web" }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Missing file: {path}");
        return File.ReadAllText(path);
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wasla.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Wasla.sln from test base directory.");
    }
}
