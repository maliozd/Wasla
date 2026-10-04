namespace Wasla.PrintBridge.Tests;



public sealed class PrintBridgeSettingsLayoutTests

{

    [Fact]

    public void SettingsLayout_UsesTwoColumnFieldTableWithInputBodyFlow()

    {

        var root = FindRepositoryRoot();

        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));

        var layout = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeSettingsLayout.cs"));



        Assert.Contains("ColumnCount = 2", layout);

        Assert.Contains("LabelColumnWidth = 150", layout);

        Assert.Contains("SideActionButtonWidth = 94", layout);

        Assert.Contains("SideActionGap = 8", layout);

        Assert.Contains("SettingsContentMaxWidth = 940", layout);

        Assert.Contains("LanguageComboWidth = 330", layout);

        Assert.Contains("CreateInputBody", layout);

        Assert.Contains("WrapContents = false", layout);

        Assert.Contains("StyleSingleLineInput", layout);

        Assert.Contains("StyleSideActionButton", layout);

        Assert.DoesNotContain("CreateFillHost", layout);

        Assert.DoesNotContain("CreateInputWithSideAction", layout);

        Assert.DoesNotContain("SideActionColumnWidth", layout);

        Assert.DoesNotContain("AddSetupCodeRow", mainForm);

        Assert.DoesNotContain("AddDeviceNameRow", mainForm);

        Assert.DoesNotContain("_settingsLayout.Layout +=", mainForm);

    }



    [Fact]

    public void SettingsSideActions_LiveInsideInputBodyFlowNextToInput()

    {

        var root = FindRepositoryRoot();

        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));

        var layout = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeSettingsLayout.cs"));



        Assert.Contains("CreateInputBody(_txtAgentToken, _btnToggleToken)", mainForm);

        Assert.Contains("CreateInputBody(_cmbPrinterName, _btnRefreshPrinters)", mainForm);

        Assert.Contains("ConfigureInputRow(table, 2)", mainForm);

        Assert.Contains("ConfigureInputRow(table, 0)", mainForm);

        Assert.Contains("StyleSideActionButton(_btnToggleToken)", mainForm);

        Assert.Contains("StyleSideActionButton(_btnRefreshPrinters)", mainForm);

        Assert.Contains("Margin = new Padding(SideActionGap, 2, 0, 0)", layout);

        Assert.Contains("MaximumSize = Size.Empty", layout);

        Assert.Contains("Multiline = false", layout);

        Assert.DoesNotContain("Dock = DockStyle.Fill", layout.Replace("///", string.Empty));

        Assert.DoesNotContain("table.Controls.Add(_btnToggleToken, 2,", mainForm);

        Assert.DoesNotContain("table.Controls.Add(_btnRefreshPrinters, 2,", mainForm);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(mainForm, @"_btnToggleToken = new Button\(\)"));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(mainForm, @"_btnRefreshPrinters = new Button\(\)"));

    }



    [Fact]

    public void CompactDensity_KeepsReadableThemeWithoutCrushingLayout()

    {

        var root = FindRepositoryRoot();

        var theme = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeUiTheme.cs"));

        var layout = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeSettingsLayout.cs"));

        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));



        Assert.Contains("BodyFont => new(\"Segoe UI\", 9F", theme);

        Assert.Contains("ActionButtonHeight => 30", theme);

        Assert.Contains("InputRowHeight = 30", layout);

        Assert.Contains("InputRowStyleHeight = 34", layout);

        Assert.InRange(ReadConst(layout, "LabelColumnWidth"), 145, 155);

        Assert.InRange(ReadConst(layout, "SideActionButtonWidth"), 92, 96);

        Assert.Contains("section.Dock = DockStyle.Top", mainForm);

        // Static layout: no resize-driven width recalculation may exist.

        Assert.DoesNotContain("ApplySettingsFieldWidths", mainForm);

        Assert.DoesNotContain("UpdateSettingsScrollLayout", mainForm);

        Assert.DoesNotContain("_settingsScrollPanel.Resize +=", mainForm);

        Assert.Contains("DoubleBufferedPanel", mainForm);

        Assert.Contains("DoubleBuffered = true", mainForm);

    }



    [Fact]

    public void CompactWidths_AreWithinRequestedRanges()

    {

        var root = FindRepositoryRoot();

        var layout = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeSettingsLayout.cs"));



        Assert.InRange(ReadConst(layout, "SettingsContentMaxWidth"), 880, 960);

        Assert.InRange(ReadConst(layout, "UrlInputWidth"), 680, 740);

        Assert.InRange(ReadConst(layout, "TokenInputWidth"), 600, 660);

        Assert.InRange(ReadConst(layout, "PrinterComboWidth"), 600, 660);

        Assert.InRange(ReadConst(layout, "LanguageComboWidth"), 300, 360);

        Assert.InRange(ReadConst(layout, "InputRowHeight"), 30, 34);

        Assert.Equal(8, ReadConst(layout, "SideActionGap"));

        Assert.InRange(ReadConst(layout, "ActionRowTopMargin"), 8, 10);

    }



    [Fact]

    public void SettingsInputs_UseFixedCompactWidths()

    {

        var root = FindRepositoryRoot();

        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));



        Assert.Contains("StyleSingleLineInput(_txtServerUrl, PrintBridgeSettingsLayout.UrlInputWidth)", mainForm);

        Assert.Contains("StyleSingleLineInput(_txtAgentToken, PrintBridgeSettingsLayout.TokenInputWidth)", mainForm);

        Assert.Contains("StyleSingleLineInput(_cmbPrinterName, PrintBridgeSettingsLayout.PrinterComboWidth)", mainForm);

        Assert.Contains("PrintBridgeSettingsLayout.LanguageComboWidth", mainForm);

    }



    private static int ReadConst(string source, string name)

    {

        var match = System.Text.RegularExpressions.Regex.Match(

            source,

            $@"public const int {name} = (?<value>\d+);");

        Assert.True(match.Success, $"Missing constant {name}");

        return int.Parse(match.Groups["value"].Value);

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
