using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

[Collection(ProcessCultureCollection.Name)]
public sealed class ShellBridgeTests : IDisposable
{
    private readonly CultureScope _cultureScope = new();
    private readonly ShellTestRig _rig;
    private readonly FakeStatusSource _source;
    private readonly FakeShellHost _host;
    private readonly FakeLanguageSwitcher _languages;
    private readonly ShellBridge _bridge;

    public ShellBridgeTests()
    {
        _rig = new ShellTestRig(Culture());
        // Printers already discovered, so the first ui.ready does not trigger a background discovery.
        _rig.Catalog.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        _source = _rig.Engine;
        _host = _rig.Host;
        _languages = _rig.Languages;
        _bridge = _rig.Bridge;
    }

    public void Dispose()
    {
        _rig.Dispose();
        _cultureScope.Dispose();
    }

    [Fact]
    public void NothingIsSentBeforeThePageIsReady()
    {
        _source.Set(Status(BridgeServerConnectionStatus.Disconnected));
        _host.RunQueued();
        _bridge.Refresh();

        Assert.Empty(_host.Sent);
    }

    [Fact]
    public void UiReady_SendsOneVersionedSnapshotOfHostState()
    {
        var outcome = _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));

        Assert.Equal(ShellMessageOutcome.Accepted, outcome);
        var message = JsonDocument.Parse(Assert.Single(_host.Sent)).RootElement;
        Assert.Equal(ShellMessageContract.Version, message.GetProperty("version").GetInt32());
        Assert.Equal("snapshot.updated", message.GetProperty("type").GetString());
        Assert.Equal(1, message.GetProperty("sequence").GetInt64());
        Assert.Equal("online", message.GetProperty("payload").GetProperty("connection").GetProperty("state").GetString());
        Assert.Equal("Kasa 1", message.GetProperty("payload").GetProperty("device").GetProperty("name").GetString());
    }

    [Fact]
    public void DuplicateUiReady_OnlyResendsStateWithIncreasingSequence()
    {
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));
        _bridge.HandleWebMessage(ShellDocument, Command("snapshot.request"));

        Assert.Equal(3, _host.Sent.Count);
        Assert.Equal(
            [1L, 2L, 3L],
            _host.Sent.Select(s => JsonDocument.Parse(s).RootElement.GetProperty("sequence").GetInt64()));
        Assert.Single(_host.Sent.Select(s => JsonDocument.Parse(s).RootElement.GetProperty("payload").GetRawText()).Distinct());
        Assert.Empty(_languages.Requests);
        Assert.Equal(0, _rig.Native.ClassicWindowRequests);
    }

    [Fact]
    public void EngineChanges_AreCoalescedIntoOneQueuedUiUpdate()
    {
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));

        Parallel.For(0, 200, _ => _source.Raise());
        _source.Set(Status(BridgeServerConnectionStatus.Error, new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ServerUnreachable)));

        Assert.Equal(1, _host.PostCalls);
        Assert.Equal(1, _host.RunQueued());
        Assert.Equal(2, _host.Sent.Count);
        Assert.Equal(
            "offline",
            JsonDocument.Parse(_host.Sent[^1]).RootElement.GetProperty("payload").GetProperty("connection").GetProperty("state").GetString());

        _source.Raise();
        Assert.Equal(2, _host.PostCalls);
    }

    [Fact]
    public void UnchangedState_IsNotResent_ButChangedStateIs()
    {
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));

        _bridge.Refresh();
        _source.Raise();
        _host.RunQueued();
        Assert.Single(_host.Sent);

        _source.Set(Status(BridgeServerConnectionStatus.Stopped, isRunning: false));
        _host.RunQueued();
        Assert.Equal(2, _host.Sent.Count);
    }

    [Fact]
    public void HiddenWindow_ReceivesNothing_UntilResend()
    {
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));
        _host.IsAvailable = false;

        _source.Set(Status(BridgeServerConnectionStatus.Stopped, isRunning: false));
        _host.RunQueued();
        Assert.Single(_host.Sent);

        _host.IsAvailable = true;
        _bridge.Resend();
        Assert.Equal(2, _host.Sent.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/index.html")]
    [InlineData("http://shell.printbridge.invalid/index.html")]
    [InlineData("https://shell.printbridge.invalid/other.html")]
    [InlineData("file:///C:/shell-ui/index.html")]
    public void MessagesFromAnyOtherSource_AreRejectedWithoutEffect(string? source)
    {
        var outcome = _bridge.HandleWebMessage(source, Command("classicWindow.open"));

        Assert.Equal(ShellMessageOutcome.Rejected, outcome);
        Assert.Equal(0, _rig.Native.ClassicWindowRequests);
        Assert.Empty(_host.Sent);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":3,"type":"host.exec","payload":{"method":"Exit"}}""")]
    [InlineData("""{"version":3,"type":"ui.ready","payload":{},"extra":1}""")]
    public void MalformedOrUnknownMessages_AreRejectedWithoutEffect(string raw)
    {
        Assert.Equal(ShellMessageOutcome.Rejected, _bridge.HandleWebMessage(ShellDocument, raw));
        Assert.Empty(_host.Sent);
        Assert.Equal(0, _rig.Native.ClassicWindowRequests);
    }

    [Fact]
    public void ClassicWindowOpen_AsksTheHost()
    {
        _bridge.HandleWebMessage(ShellDocument, Command("classicWindow.open"));

        Assert.Equal(1, _rig.Native.ClassicWindowRequests);
    }

    [Fact]
    public async Task LanguageChange_IsAppliedByTheHostAndAnsweredWithTheNewLanguage()
    {
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));

        _bridge.HandleWebMessage(ShellDocument, Command("language.change", """{"culture":"ar-SA"}"""));
        await WaitForAsync(() => _host.Sent.Count >= 2);

        Assert.Equal(["ar-SA"], _languages.Requests);
        var payload = JsonDocument.Parse(_host.Sent[^1]).RootElement.GetProperty("payload");
        Assert.Equal("ar-SA", payload.GetProperty("culture").GetString());
        Assert.Equal("rtl", payload.GetProperty("direction").GetString());
    }

    [Fact]
    public async Task FailedLanguageChange_IsAnsweredWithTheUnchangedLanguage()
    {
        _languages.Succeed = false;
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));

        _bridge.HandleWebMessage(ShellDocument, Command("language.change", """{"culture":"ru-RU"}"""));
        await WaitForAsync(() => _host.Sent.Count >= 2);

        var payload = JsonDocument.Parse(_host.Sent[^1]).RootElement.GetProperty("payload");
        Assert.Equal("tr-TR", payload.GetProperty("culture").GetString());
    }

    [Fact]
    public void AfterDispose_EngineEventsAndPageMessagesAreIgnored()
    {
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));
        _bridge.Dispose();

        _source.Raise();
        _bridge.Refresh();
        _bridge.Resend();

        Assert.Equal(ShellMessageOutcome.Ignored, _bridge.HandleWebMessage(ShellDocument, Command("ui.ready")));
        Assert.Equal(0, _host.PostCalls);
        Assert.Single(_host.Sent);
        Assert.Equal(0, _source.SubscriberCount);
    }

    [Fact]
    public void QueuedUpdate_RunningAfterDispose_SendsNothing()
    {
        _bridge.HandleWebMessage(ShellDocument, Command("ui.ready"));
        _source.Set(Status(BridgeServerConnectionStatus.Stopped, isRunning: false));
        _bridge.Dispose();

        _host.RunQueued();

        Assert.Single(_host.Sent);
    }

    [Fact]
    public void Bridge_SeesTheEngineOnlyThroughTheReadOnlyStatusSource()
    {
        var members = typeof(IPrintBridgeStatusSource)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m is not MethodInfo { IsSpecialName: true })
            .Select(m => m.Name)
            .Order()
            .ToArray();
        var bridgeDependencies = typeof(ShellBridge).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        var bridgeFields = typeof(ShellBridge).GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.FieldType).ToArray();

        Assert.Equal(["GetStatus", "StatusChanged"], members);
        Assert.DoesNotContain(typeof(PrintBridgeRuntime), bridgeDependencies);
        Assert.DoesNotContain(typeof(PrintBridgeRuntime), bridgeFields);
        Assert.DoesNotContain(typeof(WaslaPrintBridgeClient), bridgeFields);
        Assert.Contains(typeof(IPrintBridgeStatusSource), bridgeDependencies);
        Assert.True(typeof(IPrintBridgeStatusSource).IsAssignableFrom(typeof(PrintBridgeRuntime)));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
