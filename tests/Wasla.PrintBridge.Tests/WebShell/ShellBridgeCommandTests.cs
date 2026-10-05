using System.Reflection;
using System.Text.Json;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>The WAS-54 page commands, end to end from page message to host result and fresh state.</summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class ShellBridgeCommandTests : IDisposable
{
    private readonly CultureScope _cultureScope = new();
    private readonly ShellTestRig _rig;
    private readonly PrintBridgeLocalizer _localizer;

    public ShellBridgeCommandTests()
    {
        _rig = new ShellTestRig(Culture(), ShellTestRig.NewSettings(SentinelToken));
        _rig.Catalog.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        _localizer = new PrintBridgeLocalizer(_rig.Culture);
        Send(Command("ui.ready"));
    }

    public void Dispose()
    {
        _rig.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public async Task TrackedCommand_IsAnsweredWithItsResultThenFreshState()
    {
        var requestId = NewRequestId();

        Assert.Equal(ShellMessageOutcome.Accepted, Send(Tracked("printer.testPrint", requestId: requestId)));
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count == 1);

        var result = _rig.Messages(ShellMessageContract.OperationResult)[0];
        var payload = result.GetProperty("payload");
        Assert.Equal(ShellMessageContract.Version, result.GetProperty("version").GetInt32());
        Assert.Equal(requestId, payload.GetProperty("requestId").GetString());
        Assert.Equal("printer.testPrint", payload.GetProperty("operation").GetString());
        Assert.Equal("succeeded", payload.GetProperty("outcome").GetString());
        Assert.Equal(_localizer["Message.TestPrintSent"], payload.GetProperty("message").GetString());

        var sequences = _rig.Host.Sent.Select(s => JsonDocument.Parse(s).RootElement.GetProperty("sequence").GetInt64()).ToArray();
        Assert.Equal(sequences.Order(), sequences);
        Assert.Equal(sequences.Distinct().Count(), sequences.Length);
        Assert.Equal(ShellMessageContract.SnapshotUpdated, JsonDocument.Parse(_rig.Host.Sent[^1]).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RepeatedDeliveryOfOneClick_RunsAndAnswersOnce()
    {
        var raw = Tracked("printer.testPrint");

        Send(raw);
        Send(raw);
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count >= 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Single(_rig.Messages(ShellMessageContract.OperationResult));
        Assert.Equal(1, _rig.Engine.TestPrintCalls);
    }

    [Fact]
    public async Task BusyState_ComesFromTheHost_AndASecondClickIsAnsweredBusy()
    {
        _rig.Engine.TestPrintGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Send(Tracked("printer.testPrint"));
        _rig.Host.RunQueued();
        Assert.True(_rig.LastSnapshot().GetProperty("busy").GetProperty("testPrint").GetBoolean());
        Assert.False(_rig.LastSnapshot().GetProperty("busy").GetProperty("engine").GetBoolean());

        Send(Tracked("printer.testPrint"));
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count == 1);
        Assert.Equal("busy", _rig.Messages(ShellMessageContract.OperationResult)[0].GetProperty("payload").GetProperty("outcome").GetString());

        _rig.Engine.TestPrintGate.SetResult();
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count == 2);
        _rig.Host.RunQueued();
        Assert.Equal("succeeded", _rig.Messages(ShellMessageContract.OperationResult)[1].GetProperty("payload").GetProperty("outcome").GetString());
        Assert.False(_rig.LastSnapshot().GetProperty("busy").GetProperty("testPrint").GetBoolean());
        Assert.Equal(1, _rig.Engine.TestPrintCalls);
    }

    [Fact]
    public void HistoryQuery_IsAnsweredWithABoundedPageOfOpaqueRows()
    {
        var jobs = Enumerable.Range(1, 25)
            .Select(i => new LocalPrintJobRecord { JobId = Guid.NewGuid(), OrderDisplay = $"GTR-{i}", Status = LocalPrintJobStatus.Printed, CreatedAtUtc = DateTime.UtcNow })
            .ToArray();
        _rig.Engine.History = jobs;
        var requestId = NewRequestId();

        Send(Tracked("history.query", "\"range\":\"last7Days\",\"page\":1,\"search\":\"GTR\"", requestId));

        var result = Assert.Single(_rig.Messages(ShellMessageContract.HistoryResult)).GetProperty("payload");
        var history = result.GetProperty("history");
        Assert.Equal(requestId, result.GetProperty("requestId").GetString());
        Assert.Equal("last7Days", history.GetProperty("range").GetString());
        Assert.Equal(1, history.GetProperty("page").GetInt32());
        Assert.Equal(5, history.GetProperty("items").GetArrayLength());
        Assert.Equal((PrintHistoryDateFilter.Last7Days, "GTR"), _rig.Engine.LastHistoryQuery);
        var everything = string.Concat(_rig.Host.Sent);
        Assert.All(jobs, job => Assert.DoesNotContain(job.JobId.ToString("D"), everything, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HistoryFailure_IsReportedWithoutExceptionText()
    {
        _rig.Engine.HistoryFailure = new IOException(@"C:\ProgramData\Wasla\PrintBridge\print-history.json is locked by secret-process");

        Send(Tracked("history.query", "\"range\":\"today\",\"page\":0"));

        var payload = Assert.Single(_rig.Messages(ShellMessageContract.OperationResult)).GetProperty("payload");
        Assert.Equal("history.query", payload.GetProperty("operation").GetString());
        Assert.Equal("failed", payload.GetProperty("outcome").GetString());
        Assert.Equal(_localizer["Shell.Op.Failed"], payload.GetProperty("message").GetString());
        Assert.DoesNotContain("ProgramData", string.Concat(_rig.Host.Sent), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-process", string.Concat(_rig.Host.Sent), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reprint_FromThePage_UsesTheOpaqueRowHandle()
    {
        var jobId = Guid.NewGuid();
        _rig.Engine.History = [new LocalPrintJobRecord { JobId = jobId, OrderDisplay = "GTR-1001", Status = LocalPrintJobStatus.Printed, CreatedAtUtc = DateTime.UtcNow }];
        Send(Tracked("history.query", "\"range\":\"today\",\"page\":0"));
        var itemRef = _rig.Messages(ShellMessageContract.HistoryResult)[0]
            .GetProperty("payload").GetProperty("history").GetProperty("items")[0].GetProperty("ref").GetString();

        Send(Tracked("history.reprint", $"\"itemRef\":\"{itemRef}\""));
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count == 1);

        Assert.Equal([jobId], _rig.Engine.ReprintedJobs);
        Assert.Equal("succeeded", _rig.Messages(ShellMessageContract.OperationResult)[0].GetProperty("payload").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ConnectionSetup_OpensTheNativeDialog_NotTheClassicWindow_AndAnswersWithItsLocalizedResult()
    {
        var requestId = NewRequestId();
        _rig.Native.SetupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.Equal(ShellMessageOutcome.Accepted, Send(Tracked("connection.openSetup", requestId: requestId)));

        // While the dialog is open the page sees host busy state; a second click is answered busy, not run again.
        Assert.Equal(1, _rig.Native.ConnectionSetupRequests);
        Assert.True(_rig.Operations.Busy.ConnectionSetup);
        Send(Tracked("connection.openSetup"));
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count == 1);
        Assert.Equal("busy", _rig.Messages(ShellMessageContract.OperationResult)[0].GetProperty("payload").GetProperty("outcome").GetString());

        _rig.Native.SetupGate.SetResult();
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count == 2);

        var payload = _rig.Messages(ShellMessageContract.OperationResult)[1].GetProperty("payload");
        Assert.Equal(requestId, payload.GetProperty("requestId").GetString());
        Assert.Equal("connection.openSetup", payload.GetProperty("operation").GetString());
        Assert.Equal("succeeded", payload.GetProperty("outcome").GetString());
        Assert.Equal(1, _rig.Native.ConnectionSetupRequests);
        Assert.Equal(0, _rig.Native.ClassicWindowRequests);
        Assert.Equal(0, _rig.Engine.ResetCalls);
        Assert.False(_rig.Operations.Busy.ConnectionSetup);
    }

    [Fact]
    public async Task ConnectionSetup_ConnectedWithoutAPrinter_SendsThePageToThePrinterTab()
    {
        _rig.Native.SetupResult = new ShellConnectionSetupResult(ShellConnectionSetupOutcome.ConnectedChoosePrinter, "choose a printer");

        Send(Tracked("connection.openSetup"));
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.UiNavigate).Count == 1);

        Assert.Equal("printer", _rig.Messages(ShellMessageContract.UiNavigate)[0].GetProperty("payload").GetProperty("tab").GetString());
        var types = _rig.Host.SentSoFar().Select(s => JsonDocument.Parse(s).RootElement.GetProperty("type").GetString()).ToArray();
        // The result and fresh state come first, then the navigation.
        Assert.Equal(ShellMessageContract.UiNavigate, types[^1]);
        Assert.Contains(ShellMessageContract.OperationResult, types);
    }

    [Fact]
    public void Navigate_WaitsForThePage_AndIgnoresUnknownTabs()
    {
        using var rig = new ShellTestRig(Culture(), ShellTestRig.NewSettings(SentinelToken));

        rig.Bridge.Navigate("history");
        rig.Bridge.Navigate("../classic");
        Assert.Empty(rig.Messages(ShellMessageContract.UiNavigate));

        rig.Bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));

        var navigation = Assert.Single(rig.Messages(ShellMessageContract.UiNavigate));
        Assert.Equal("history", navigation.GetProperty("payload").GetProperty("tab").GetString());
        // The page first receives state, then the tab request.
        Assert.Equal(ShellMessageContract.SnapshotUpdated, JsonDocument.Parse(rig.Host.SentSoFar()[0]).RootElement.GetProperty("type").GetString());

        rig.Bridge.Navigate("settings");
        Assert.Equal("settings", rig.Messages(ShellMessageContract.UiNavigate)[^1].GetProperty("payload").GetProperty("tab").GetString());
    }

    [Fact]
    public void SetupLinkResult_IsShownWithoutARequestId_OnceThePageIsReady()
    {
        using var rig = new ShellTestRig(Culture(), ShellTestRig.NewSettings(SentinelToken));

        rig.Bridge.NotifyConnectionResult(ShellOperationOutcome.Succeeded, "connected", navigateTo: "printer");
        Assert.Empty(rig.Host.Sent);

        rig.Bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));

        var result = Assert.Single(rig.Messages(ShellMessageContract.OperationResult)).GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("requestId").ValueKind);
        Assert.Equal("connection.openSetup", result.GetProperty("operation").GetString());
        Assert.Equal("connected", result.GetProperty("message").GetString());
        Assert.Equal("printer", Assert.Single(rig.Messages(ShellMessageContract.UiNavigate)).GetProperty("payload").GetProperty("tab").GetString());
    }

    [Fact]
    public async Task NoHostMessageEverCarriesTheTokenServerUrlOrMachineName()
    {
        Send(Tracked("connection.test"));
        Send(Tracked("printer.testPrint"));
        Send(Tracked("history.query", "\"range\":\"today\",\"page\":0"));
        Send(Command("language.change", """{"culture":"ar-SA"}"""));
        await WaitForAsync(() => _rig.Messages(ShellMessageContract.OperationResult).Count == 2);
        _rig.Host.RunQueued();

        var everything = string.Concat(_rig.Host.Sent);
        Assert.DoesNotContain(SentinelToken, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SENTINEL", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SentinelServerUrl, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QA-MACHINE", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, everything, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"version":3,"type":"logs.openFolder","payload":{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","path":"C:\\Windows"}}""")]
    [InlineData("""{"version":3,"type":"logs.openFolder","payload":{"path":"C:\\Windows"}}""")]
    [InlineData("""{"version":3,"type":"connection.openSetup","payload":{"serverUrl":"https://evil.example"}}""")]
    [InlineData("""{"version":3,"type":"connection.openSetup","payload":{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","token":"pb_x"}}""")]
    [InlineData("""{"version":2,"type":"connection.openSetup","payload":{}}""")]
    [InlineData("""{"version":3,"type":"settings.save","payload":{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","key":"OrderHub:AgentToken","value":"pb_x"}}""")]
    [InlineData("""{"version":3,"type":"connection.reset","payload":{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","token":"pb_x"}}""")]
    [InlineData("""{"version":3,"type":"classicWindow.open","payload":{"url":"https://evil.example"}}""")]
    [InlineData("""{"version":3,"type":"history.reprint","payload":{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","itemRef":"4b6f0c8e-1f8a-4f5e-9d9e-0f3a9c000001"}}""")]
    [InlineData("""{"version":3,"type":"history.reprint","payload":{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","jobId":"4b6f0c8e-1f8a-4f5e-9d9e-0f3a9c000001"}}""")]
    [InlineData("""{"version":3,"type":"printer.save","payload":{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","name":"POS-58","port":"LPT1"}}""")]
    [InlineData("""{"version":3,"type":"printer.testPrint","payload":{}}""")]
    [InlineData("""{"version":3,"type":"engine.start","payload":{"requestId":"short"}}""")]
    public void CommandsCarryingPathsUrlsCredentialsOrRawIds_AreRejectedWithoutEffect(string raw)
    {
        var sentBefore = _rig.Host.Sent.Count;

        Assert.Equal(ShellMessageOutcome.Rejected, Send(raw));

        Assert.Equal(sentBefore, _rig.Host.Sent.Count);
        Assert.Equal(0, _rig.Native.LogFolderRequests + _rig.Native.ConnectionSetupRequests + _rig.Native.ClassicWindowRequests);
        Assert.Equal(0, _rig.Engine.ResetCalls + _rig.Engine.ReprintCalls + _rig.Engine.TestPrintCalls + _rig.Engine.StartCalls);
        Assert.Equal(0, _rig.Engine.CheckConnectionCalls + _rig.Engine.ApplyConnectionCalls);
        Assert.Empty(_rig.PrinterSettings.Saved);
        Assert.Empty(_rig.OperationalSettings.Saved);
    }

    [Fact]
    public void PageReadyAndRefreshes_NeverStartTheEngineOrPollTheServer()
    {
        for (var i = 0; i < 20; i++)
        {
            Send(Command("ui.ready"));
            Send(Command("snapshot.request"));
            _rig.Bridge.Refresh();
        }

        Assert.Equal(0, _rig.Engine.StartCalls);
        Assert.Equal(0, _rig.Engine.TestConnectionCalls);
        Assert.Null(_rig.Engine.LastHistoryQuery);
    }

    [Fact]
    public void ShellTypes_OwnNoPollingLoopTimersOrServerClient()
    {
        var forbidden = new[]
        {
            typeof(System.Threading.Timer), typeof(PeriodicTimer), typeof(System.Timers.Timer), typeof(Thread),
            typeof(HttpClient), typeof(WaslaPrintBridgeClient), typeof(PrintBridgeRuntime)
        };

        // Every WebView2 shell type, including the window: the only periodic work there is the UI refresh timer,
        // which re-reads engine state. Polling, claiming and printing stay in PrintBridgeRuntime.
        var shellTypes = typeof(ShellBridge).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(ShellBridge).Namespace)
            .ToArray();
        Assert.Contains(typeof(PrintBridgeShellForm), shellTypes);
        foreach (var type in shellTypes)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Assert.DoesNotContain(fields, f => forbidden.Contains(f.FieldType));
        }

        // Operations reach the engine only through the narrow engine interface, never the concrete runtime.
        Assert.Contains(typeof(IPrintBridgeEngine), typeof(ShellOperations).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        Assert.DoesNotContain(typeof(PrintBridgeRuntime), typeof(PrintBridgeShellForm).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
    }

    private ShellMessageOutcome Send(string raw) => _rig.Bridge.HandleWebMessage(ShellDocument, raw);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
