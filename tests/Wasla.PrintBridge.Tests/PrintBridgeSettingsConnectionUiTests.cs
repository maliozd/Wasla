namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeSettingsConnectionUiTests
{
    [Fact]
    public void SettingsTab_DoesNotRenderManualSetupCodeInput()
    {
        var root = FindRepositoryRoot();
        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));
        var settingsSave = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.SettingsSave.cs"));

        Assert.DoesNotContain("AddSetupCodeRow", mainForm);
        Assert.DoesNotContain("_txtSetupCode", mainForm);
        Assert.DoesNotContain("_btnConnectSetupCode", mainForm);
        Assert.DoesNotContain("_deviceGroup", mainForm);
        Assert.DoesNotContain("ConnectWithSetupCodeAsync", settingsSave);
        Assert.DoesNotContain("SaveDeviceIdentitySettingsAsync", settingsSave);
        Assert.DoesNotContain("Settings.SetupCode", mainForm);
    }

    [Fact]
    public void SettingsTab_StillRendersCoreConnectionActions()
    {
        var root = FindRepositoryRoot();
        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));

        Assert.Contains("_btnSaveConnection", mainForm);
        Assert.Contains("_btnTestSettingsConnection", mainForm);
        Assert.Contains("_btnResetConnection", mainForm);
        Assert.Contains("AddServerUrlRow", mainForm);
        Assert.Contains("AddTokenRow", mainForm);
    }

    [Fact]
    public void ResetFollowUpCopy_PointsToWebSetupPage_NotDesktopSetupCodeEntry()
    {
        var root = FindRepositoryRoot();
        var resourcesDir = Path.Combine(root, "src", "Wasla.PrintBridge", "Resources");

        var tr = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.tr-TR.resx",
            "RuntimeIssue.ManualReset.Detail");
        var en = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.en-US.resx",
            "RuntimeIssue.ManualReset.Detail");

        Assert.Contains("Wasla Web", tr, StringComparison.Ordinal);
        Assert.Contains("kurulum sayfas", tr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("buraya gir", tr, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Wasla Web panel", en, StringComparison.Ordinal);
        Assert.Contains("Print Bridge setup page", en, StringComparison.Ordinal);
        Assert.DoesNotContain("enter it here", en, StringComparison.OrdinalIgnoreCase);

        foreach (var culture in new[]
                 {
                     "PrintBridgeResources.resx",
                     "PrintBridgeResources.tr-TR.resx",
                     "PrintBridgeResources.en-US.resx",
                     "PrintBridgeResources.ar-SA.resx",
                     "PrintBridgeResources.ru-RU.resx"
                 })
        {
            var content = File.ReadAllText(Path.Combine(resourcesDir, culture));
            Assert.Contains("RuntimeIssue.ManualReset.Detail", content);
        }
    }

    [Fact]
    public void AutomaticProtocolSetup_InfrastructureStillExists()
    {
        var root = FindRepositoryRoot();
        var coordinator = Path.Combine(root, "src", "Wasla.PrintBridge", "Setup", "PrintBridgeAutoSetupCoordinator.cs");
        var protocol = Path.Combine(root, "src", "Wasla.PrintBridge", "Setup", "PrintBridgeProtocolUri.cs");
        var tray = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "TrayApplicationContext.cs"));

        Assert.True(File.Exists(coordinator));
        Assert.True(File.Exists(protocol));
        Assert.Contains("RefreshAfterAutomaticSetup", tray);
    }

    [Fact]
    public void PrintHistoryToolbar_UsesCompactPrimaryReprintButton()
    {
        var root = FindRepositoryRoot();
        var history = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.PrintHistory.cs"));
        var theme = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeUiTheme.cs"));

        Assert.Contains("CreateToolbarPrimaryButton", history);
        Assert.Contains("ApplyToolbarPrimaryButtonWidth(_btnReprint)", history);
        Assert.Contains("ApplyToolbarPrimaryButtonWidth", theme);
    }

    [Fact]
    public void ResetConnectionButton_UsesStackedLayoutOutsideFieldTable()
    {
        var root = FindRepositoryRoot();
        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));
        var layout = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeSettingsLayout.cs"));
        var theme = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeUiTheme.cs"));
        var resources = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "Resources", "PrintBridgeResources.tr-TR.resx"));

        Assert.Contains("_btnResetConnection", mainForm);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(mainForm, @"out _btnResetConnection"));
        Assert.Contains("CreateTroubleshootingRow", mainForm);
        Assert.Contains("connectionContent.Controls.Add(troubleshootingRow", mainForm);
        Assert.DoesNotContain("connectionLayout.Controls.Add(troubleshootingRow", mainForm);
        Assert.DoesNotContain("SetColumnSpan(troubleshootingRow", mainForm);
        Assert.DoesNotContain("_settingsLayout.Layout +=", mainForm);
        Assert.Contains("_lblAgentTokenPasteGuard", mainForm);
        Assert.Contains("RefreshAgentTokenPasteGuard", mainForm);

        Assert.Contains("ApplyTroubleshootingActionButtonLayout", layout);
        Assert.Contains("MinimumSize = new Size(160, PrintBridgeUiTheme.ActionButtonHeight)", layout);
        Assert.Contains("Anchor = AnchorStyles.Left", layout);
        Assert.Contains("Margin = new Padding(0, 8, 0, 0)", layout);
        Assert.DoesNotContain("CenterChildVertically", layout);
        Assert.DoesNotContain("buttonHost", layout);
        Assert.DoesNotContain("PrepareStretchingInput", layout);
        Assert.Contains("section.Dock = DockStyle.Top", mainForm);

        Assert.Contains("CreateRecoveryOutlineButton", theme);
        Assert.Contains("MinimumSize = new Size(160, ActionButtonHeight)", theme);
        Assert.Contains("ActionButtonHeight => 30", theme);
        Assert.Contains("Button.ResetConnection", resources);
        Assert.Contains("Bağlantıyı sıfırla", resources);
        Assert.Contains("Settings.AgentTokenPasteGuard", resources);
    }

    [Fact]
    public void TokenAndPrinterRows_KeepSingleSideActionButtons()
    {
        var root = FindRepositoryRoot();
        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));

        Assert.Contains("StyleSideActionButton(_btnToggleToken)", mainForm);
        Assert.Contains("StyleSideActionButton(_btnRefreshPrinters)", mainForm);
        Assert.Contains("CreateInputBody(_txtAgentToken, _btnToggleToken)", mainForm);
        Assert.Contains("CreateInputBody(_cmbPrinterName, _btnRefreshPrinters)", mainForm);
        Assert.Contains("StyleSingleLineInput(_txtAgentToken, PrintBridgeSettingsLayout.TokenInputWidth)", mainForm);
        Assert.DoesNotContain("CreateInputWithSideAction", mainForm);
        Assert.DoesNotContain("CreateFillHost", mainForm);
        Assert.DoesNotContain("table.Controls.Add(_btnToggleToken, 2,", mainForm);
        Assert.DoesNotContain("table.Controls.Add(_btnRefreshPrinters, 2,", mainForm);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(mainForm, @"_btnToggleToken = new Button\(\)"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(mainForm, @"_btnRefreshPrinters = new Button\(\)"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(mainForm, @"out _btnResetConnection"));
    }

    [Fact]
    public void TokenTyping_DoesNotRemoveTroubleshootingSection()
    {
        var root = FindRepositoryRoot();
        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));
        var settingsSave = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.SettingsSave.cs"));

        Assert.Contains("CreateTroubleshootingRow", mainForm);
        Assert.Contains("_lblConnectionTroubleshootingTitle", mainForm);
        Assert.Contains("_lblResetConnectionHelp", mainForm);
        Assert.Contains("_btnResetConnection", mainForm);
        Assert.DoesNotContain("Controls.Remove(_btnResetConnection", mainForm);
        Assert.DoesNotContain("Controls.Remove(troubleshootingRow", mainForm);
        Assert.Contains("RefreshAgentTokenPasteGuard", settingsSave);
        Assert.Contains("SetSectionStatus(_lblConnectionStatus, null)", settingsSave);
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "src", "Wasla.PrintBridge", "UI")))
                return current;

            var parent = Directory.GetParent(current);
            if (parent is null)
                break;
            current = parent.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
