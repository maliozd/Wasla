using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>Which actions the app offers, and what Overview, Printer and Diagnostics show, all from host truth.</summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class ShellSnapshotActionsTests : IDisposable
{
    private readonly CultureScope _cultureScope = new();
    private readonly ShellTestRig _rig;
    private readonly PrintBridgeLocalizer _localizer;

    public ShellSnapshotActionsTests()
    {
        _rig = new ShellTestRig(Culture());
        _localizer = new PrintBridgeLocalizer(_rig.Culture);
    }

    public void Dispose()
    {
        _rig.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public void RunningAndConnected_OffersStopTestPrintAndChecks()
    {
        var actions = _rig.Factory.Create(Status()).Actions;

        Assert.Equal(new ShellActionsView(
            Start: false,
            Stop: true,
            TestPrint: true,
            CheckConnection: true,
            Reconnect: false,
            ConfigurePrinter: false,
            ResetConnection: true), actions);
    }

    [Fact]
    public void Stopped_OffersStart()
    {
        var snapshot = _rig.Factory.Create(Status(BridgeServerConnectionStatus.Stopped, isRunning: false));

        Assert.True(snapshot.Actions.Start);
        Assert.False(snapshot.Actions.Stop);
        Assert.Equal(new ShellEngineView("stopped", _localizer["Status.Stopped"]), snapshot.Engine);
    }

    [Theory]
    [InlineData(PrintBridgeRuntimeIssueCode.ReconnectRequired)]
    [InlineData(PrintBridgeRuntimeIssueCode.DuplicateInstallation)]
    public void BlockingIssues_WithholdStartAndOfferReconnect(PrintBridgeRuntimeIssueCode code)
    {
        var actions = _rig.Factory.Create(Status(BridgeServerConnectionStatus.Error, new PrintBridgeRuntimeIssue(code), isRunning: false)).Actions;

        Assert.False(actions.Start);
        Assert.False(actions.CheckConnection);
        Assert.True(actions.Reconnect);
    }

    [Fact]
    public void MissingToken_WithholdsStartAndReset_AndOffersReconnect()
    {
        using var rig = new ShellTestRig(Culture(), ShellTestRig.NewSettings("  "));

        var actions = rig.Factory.Create(Status(BridgeServerConnectionStatus.NotConfigured, isRunning: false)).Actions;

        Assert.False(actions.Start);
        Assert.False(actions.ResetConnection);
        Assert.False(actions.CheckConnection);
        Assert.True(actions.Reconnect);
    }

    [Theory]
    [InlineData(PrinterHealthStatus.Ready, "ready", true, false)]
    [InlineData(PrinterHealthStatus.NotFound, "notFound", false, true)]
    [InlineData(PrinterHealthStatus.NotConfigured, "notConfigured", false, true)]
    [InlineData(PrinterHealthStatus.DryRun, "dryRun", false, false)]
    public void PrinterHealth_DecidesTestPrintAndPrinterSetup(PrinterHealthStatus health, string state, bool testPrint, bool configure)
    {
        var status = Status(dryRun: health == PrinterHealthStatus.DryRun, printerHealth: health);

        var snapshot = _rig.Factory.Create(status);

        Assert.Equal(state, snapshot.Printer.State);
        Assert.Equal(_localizer.GetPrinterHealthStatus(health), snapshot.Printer.Label);
        Assert.Equal(testPrint, snapshot.Actions.TestPrint);
        Assert.Equal(configure, snapshot.Actions.ConfigurePrinter);
        Assert.Equal(health == PrinterHealthStatus.DryRun, snapshot.DryRun);
    }

    [Fact]
    public async Task InstalledPrinters_ComeFromTheHostCatalog()
    {
        Assert.Empty(_rig.Factory.Create(Status()).Printer.Installed);

        await _rig.Catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Microsoft Print to PDF", "POS-58"], _rig.Factory.Create(Status()).Printer.Installed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAServerDeviceName_TheDeviceIsUnknown_NeverTheMachineName(string displayName)
    {
        var snapshot = _rig.Factory.Create(Status(displayName: displayName));

        Assert.Equal(_localizer["Shell.Device.Unknown"], snapshot.Device.Name);
        Assert.DoesNotContain("QA-MACHINE", ShellMessageSerializer.SerializePayload(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_ShowVersionsStateAndTestMode()
    {
        var snapshot = _rig.Factory.Create(Status(dryRun: true));

        Assert.Equal("v1.0.0", snapshot.Diagnostics.AppVersion);
        Assert.Equal("154.0.4258.53", snapshot.Diagnostics.WebView2Version);
        Assert.Equal(_localizer["Status.Running"], snapshot.Diagnostics.EngineLabel);
        Assert.Equal(_localizer["Shell.Settings.TestModeOn"], snapshot.Diagnostics.TestModeLabel);
        Assert.Equal("POS-58", snapshot.Diagnostics.PrinterName);
        Assert.Null(snapshot.Diagnostics.LastIssue);
        Assert.Equal(_localizer["Shell.Settings.TestModeOff"], _rig.Factory.Create(Status()).Diagnostics.TestModeLabel);
    }

    [Fact]
    public void Diagnostics_WithoutAWebView2Version_SaysUnavailable()
    {
        var factory = new ShellSnapshotFactory(_localizer, _rig.Culture, _rig.Settings, _rig.Catalog, () => ShellBusyState.Idle, webView2Version: null);

        Assert.Equal(_localizer["Shell.Diagnostics.Unavailable"], factory.Create(Status()).Diagnostics.WebView2Version);
    }

    [Fact]
    public void Diagnostics_ShowTheErrorCategory_NeverRawExceptionText()
    {
        const string raw = "System.Net.Http.HttpRequestException: secret-host.internal:4431 refused";
        var unexpected = _rig.Factory.Create(Status(BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssue.FromRaw(raw)));
        var offline = _rig.Factory.Create(Status(BridgeServerConnectionStatus.Error, new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable)));

        Assert.Equal(_localizer["Shell.Detail.UnexpectedError"], unexpected.Diagnostics.LastIssue);
        Assert.Equal(
            _localizer.GetRuntimeIssueDetail(new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable)),
            offline.Diagnostics.LastIssue);
        Assert.DoesNotContain("secret-host", ShellMessageSerializer.SerializePayload(unexpected), StringComparison.Ordinal);
    }

    [Fact]
    public void BusyFlags_AreTheHostOperationState()
    {
        var busy = new ShellBusyState(
            Engine: false, ConnectionTest: true, ConnectionReset: false, PrintersRefresh: false, PrinterSave: false,
            TestPrint: true, Reprint: false, ConnectionSetup: true, SettingsSave: false);
        var factory = new ShellSnapshotFactory(_localizer, _rig.Culture, _rig.Settings, _rig.Catalog, () => busy, "154.0.4258.53");

        Assert.Same(busy, factory.Create(Status()).Busy);
        Assert.Equal(ShellBusyState.Idle, _rig.Factory.Create(Status()).Busy);
    }

    [Fact]
    public void Operational_ShowsTheSavedValuesTheEngineUses_WithTheValidatorRanges()
    {
        _rig.Settings.Replace(
            _rig.Settings.OrderHub,
            new Wasla.PrintBridge.Options.PrintBridgeOptions
            {
                PrinterName = "POS-58",
                DryRun = true,
                IdlePollIntervalSeconds = 9,
                BusyPollIntervalSeconds = 2,
                ErrorPollIntervalSeconds = 40
            });

        var operational = _rig.Factory.Create(Status(dryRun: true)).Operational;

        Assert.True(operational.TestMode);
        Assert.Equal(new ShellSecondsSetting(9, 1, 300, _localizer.GetString("Shell.Settings.Range", 1, 300)), operational.IdlePoll);
        Assert.Equal(new ShellSecondsSetting(2, 1, 60, _localizer.GetString("Shell.Settings.Range", 1, 60)), operational.BusyPoll);
        Assert.Equal(new ShellSecondsSetting(40, 1, 300, _localizer.GetString("Shell.Settings.Range", 1, 300)), operational.ErrorPoll);
        Assert.Equal(PrintBridgeSettingsValidator.MaxIdlePollIntervalSeconds, operational.IdlePoll.Max);
        Assert.Equal(PrintBridgeSettingsValidator.MaxBusyPollIntervalSeconds, operational.BusyPoll.Max);
    }

    [Fact]
    public void Strings_DoNotIncludeThePreviewNotice()
    {
        Assert.DoesNotContain("Shell.Preview.Notice", ShellSnapshotFactory.StringKeys);
        Assert.DoesNotContain("Shell.Preview.Notice", File.ReadAllText(Path.Combine(AssetsDirectory(), "index.html")), StringComparison.Ordinal);
    }
}
