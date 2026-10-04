using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

public sealed class ShellMessageParserTests
{
    [Theory]
    [InlineData("ui.ready", ShellCommandType.UiReady)]
    [InlineData("snapshot.request", ShellCommandType.SnapshotRequest)]
    [InlineData("classicWindow.open", ShellCommandType.ClassicWindowOpen)]
    public void AcceptsEachAllowlistedCommand(string type, ShellCommandType expected)
    {
        Assert.True(ShellMessageParser.TryParse(Command(type), out var command, out var rejection));

        Assert.Equal(ShellMessageRejection.None, rejection);
        Assert.Equal(new ShellCommand(expected), command);
    }

    [Fact]
    public void AcceptsCommandWithoutPayload()
    {
        Assert.True(ShellMessageParser.TryParse("""{"version":2,"type":"ui.ready"}""", out var command, out _));
        Assert.Equal(ShellCommandType.UiReady, command!.Type);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("ru-RU")]
    public void AcceptsLanguageChangeForEverySupportedCulture(string culture)
    {
        Assert.True(ShellMessageParser.TryParse(
            Command("language.change", $$"""{"culture":"{{culture}}"}"""),
            out var command,
            out _));

        Assert.Equal(new ShellCommand(ShellCommandType.LanguageChange, Culture: culture), command);
    }

    [Fact]
    public void ContractListsExactlyTheParsedCommands()
    {
        Assert.Equal(
            [
                "ui.ready", "snapshot.request", "language.change", "classicWindow.open",
                "engine.start", "engine.stop", "connection.test", "connection.openSetup", "connection.reset",
                "printers.refresh", "printer.save", "printer.testPrint", "history.query", "history.reprint",
                "logs.openFolder"
            ],
            ShellMessageContract.CommandTypes);
        Assert.Equal(ShellMessageContract.CommandTypes.Count, Enum.GetValues<ShellCommandType>().Length);
    }

    [Theory]
    [InlineData("engine.start", ShellCommandType.EngineStart)]
    [InlineData("engine.stop", ShellCommandType.EngineStop)]
    [InlineData("connection.test", ShellCommandType.ConnectionTest)]
    [InlineData("connection.reset", ShellCommandType.ConnectionReset)]
    [InlineData("printers.refresh", ShellCommandType.PrintersRefresh)]
    [InlineData("printer.testPrint", ShellCommandType.PrinterTestPrint)]
    [InlineData("logs.openFolder", ShellCommandType.LogsOpenFolder)]
    public void AcceptsTrackedCommandsWithOnlyARequestId(string type, ShellCommandType expected)
    {
        const string requestId = "a1b2c3d4-0000-4000-8000-000000000001";

        Assert.True(ShellMessageParser.TryParse(Tracked(type, requestId: requestId), out var command, out _));

        Assert.Equal(new ShellCommand(expected, requestId), command);
    }

    [Fact]
    public void AcceptsConnectionSetupWithoutAnyPayload()
    {
        Assert.True(ShellMessageParser.TryParse(Command("connection.openSetup"), out var command, out _));
        Assert.Equal(new ShellCommand(ShellCommandType.ConnectionOpenSetup), command);
    }

    [Theory]
    [InlineData("POS-58")]
    [InlineData("Microsoft Print to PDF")]
    [InlineData(@"\\\\print-server\\Kitchen")]
    [InlineData("Ödeme Yazıcısı")]
    [InlineData("طابعة الإيصالات")]
    public void AcceptsPrinterSaveWithAPrinterName(string jsonName)
    {
        Assert.True(ShellMessageParser.TryParse(Tracked("printer.save", $"\"name\":\"{jsonName}\""), out var command, out _));

        Assert.Equal(ShellCommandType.PrinterSave, command!.Type);
        Assert.Equal(jsonName.Replace(@"\\", @"\"), command.PrinterName);
    }

    [Fact]
    public void AcceptsPrinterNamesUpToTheLimit()
    {
        var name = new string('P', ShellMessageContract.MaxPrinterNameLength);

        Assert.True(ShellMessageParser.TryParse(Tracked("printer.save", $"\"name\":\"{name}\""), out _, out _));
        Assert.False(ShellMessageParser.TryParse(Tracked("printer.save", $"\"name\":\"{name}P\""), out _, out var rejection));
        Assert.Equal(ShellMessageRejection.InvalidPayload, rejection);
    }

    [Theory]
    [InlineData("today", PrintHistoryDateFilter.Today)]
    [InlineData("last7Days", PrintHistoryDateFilter.Last7Days)]
    [InlineData("last30Days", PrintHistoryDateFilter.Last30Days)]
    public void AcceptsHistoryQueriesForEachRange(string range, PrintHistoryDateFilter expected)
    {
        Assert.True(ShellMessageParser.TryParse(Tracked("history.query", $"\"range\":\"{range}\",\"page\":3,\"search\":\"GTR-1\""), out var command, out _));

        Assert.Equal(expected, command!.HistoryRange);
        Assert.Equal(3, command.HistoryPage);
        Assert.Equal("GTR-1", command.HistorySearch);
    }

    [Fact]
    public void AcceptsHistoryQueryWithoutSearch_AndReprintWithAnOpaqueRef()
    {
        Assert.True(ShellMessageParser.TryParse(Tracked("history.query", "\"range\":\"today\",\"page\":0"), out var query, out _));
        Assert.True(ShellMessageParser.TryParse(Tracked("history.reprint", "\"itemRef\":\"h0123456789abcdef\""), out var reprint, out _));

        Assert.Null(query!.HistorySearch);
        Assert.Equal("h0123456789abcdef", reprint!.HistoryItemRef);
    }

    [Theory]
    // Request ids: required, bounded, no separators or paths.
    [InlineData("engine.start", "{}")]
    [InlineData("engine.start", """{"requestId":"short"}""")]
    [InlineData("engine.start", """{"requestId":"-a1b2c3d4-0000"}""")]
    [InlineData("engine.start", """{"requestId":"a1b2c3d4/../0000"}""")]
    [InlineData("engine.start", """{"requestId":"a1b2c3d4 0000 4000"}""")]
    [InlineData("engine.start", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001\n"}""")]
    [InlineData("engine.start", """{"requestId":42}""")]
    [InlineData("engine.start", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","requestId":"a1b2c3d4-0000-4000-8000-000000000002"}""")]
    [InlineData("engine.stop", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","force":true}""")]
    [InlineData("connection.openSetup", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001"}""")]
    [InlineData("connection.openSetup", """{"serverUrl":"https://evil.example","token":"x"}""")]
    [InlineData("connection.reset", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","token":""}""")]
    [InlineData("logs.openFolder", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","path":"C:\\Windows"}""")]
    // Printer names: present, trimmed, bounded, no control characters.
    [InlineData("printer.save", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001"}""")]
    [InlineData("printer.save", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","name":""}""")]
    [InlineData("printer.save", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","name":" POS-58"}""")]
    [InlineData("printer.save", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","name":"POS-58 "}""")]
    [InlineData("printer.save", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","name":"POS\n58"}""")]
    [InlineData("printer.save", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","name":"POS\u000058"}""")]
    [InlineData("printer.save", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","name":7}""")]
    [InlineData("printer.save", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","name":"POS-58","mode":"Raw"}""")]
    // History: known ranges, bounded pages and searches, opaque refs only.
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","page":0}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"all","page":0}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"Today","page":0}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"today"}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"today","page":-1}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"today","page":1001}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"today","page":1.5}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"today","page":"1"}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"today","page":0,"search":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("history.query", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","range":"today","page":0,"search":"a\tb"}""")]
    [InlineData("history.reprint", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001"}""")]
    [InlineData("history.reprint", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","itemRef":"4b6f0c8e-1f8a-4f5e-9d9e-0f3a9c000001"}""")]
    [InlineData("history.reprint", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","itemRef":"h0123456789ABCDEF"}""")]
    [InlineData("history.reprint", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","itemRef":"h0123456789abcde"}""")]
    [InlineData("history.reprint", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","itemRef":"h0123456789abcdef\n"}""")]
    [InlineData("history.reprint", """{"requestId":"a1b2c3d4-0000-4000-8000-000000000001","itemRef":"h0123456789abcdef","copies":3}""")]
    public void RejectsInvalidCommandPayloads(string type, string payload)
    {
        Assert.False(ShellMessageParser.TryParse(Command(type, payload), out var command, out var rejection));

        Assert.Null(command);
        Assert.Equal(ShellMessageRejection.InvalidPayload, rejection);
    }

    [Theory]
    [InlineData("host.exec")]
    [InlineData("Dispose")]
    [InlineData("Start")]
    [InlineData("UI.READY")]
    [InlineData("ui.ready ")]
    [InlineData("snapshot.updated")]
    [InlineData("")]
    public void RejectsUnknownTypes(string type)
    {
        Assert.False(ShellMessageParser.TryParse(Command(type), out var command, out var rejection));

        Assert.Null(command);
        Assert.Equal(ShellMessageRejection.UnknownType, rejection);
    }

    [Theory]
    [InlineData("""{"version":2,"type":42}""", ShellMessageRejection.UnknownType)]
    [InlineData("""{"version":2}""", ShellMessageRejection.UnknownType)]
    [InlineData("""{"type":"ui.ready"}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":3,"type":"ui.ready"}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":1,"type":"ui.ready","payload":{}}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":"1","type":"ui.ready"}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":1.5,"type":"ui.ready"}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":2,"type":"ui.ready","method":"Exit"}""", ShellMessageRejection.UnexpectedProperty)]
    [InlineData("""{"version":2,"type":"ui.ready","Type":"x"}""", ShellMessageRejection.UnexpectedProperty)]
    [InlineData("""{"version":2,"type":"ui.ready","type":"classicWindow.open"}""", ShellMessageRejection.DuplicateProperty)]
    [InlineData("""{"version":2,"version":2,"type":"ui.ready"}""", ShellMessageRejection.DuplicateProperty)]
    [InlineData("""{"version":2,"type":"ui.ready","payload":[]}""", ShellMessageRejection.InvalidPayload)]
    [InlineData("""{"version":2,"type":"ui.ready","payload":"x"}""", ShellMessageRejection.InvalidPayload)]
    [InlineData("""{"version":2,"type":"ui.ready","payload":{"force":true}}""", ShellMessageRejection.InvalidPayload)]
    [InlineData("""{"version":2,"type":"classicWindow.open","payload":{"tab":"settings"}}""", ShellMessageRejection.InvalidPayload)]
    public void RejectsMalformedEnvelopes(string raw, ShellMessageRejection expected)
    {
        Assert.False(ShellMessageParser.TryParse(raw, out var command, out var rejection));

        Assert.Null(command);
        Assert.Equal(expected, rejection);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"culture":"de-DE"}""")]
    [InlineData("""{"culture":"tr-tr"}""")]
    [InlineData("""{"culture":" tr-TR"}""")]
    [InlineData("""{"culture":""}""")]
    [InlineData("""{"culture":null}""")]
    [InlineData("""{"culture":7}""")]
    [InlineData("""{"culture":"tr-TR","path":"C:\\x"}""")]
    [InlineData("""{"culture":"tr-TR","culture":"en-US"}""")]
    [InlineData("""{"Culture":"tr-TR"}""")]
    public void RejectsInvalidLanguagePayloads(string payload)
    {
        Assert.False(ShellMessageParser.TryParse(Command("language.change", payload), out _, out var rejection));
        Assert.Equal(ShellMessageRejection.InvalidPayload, rejection);
    }

    [Fact]
    public void RejectsLanguageChangeWithoutPayload()
    {
        Assert.False(ShellMessageParser.TryParse("""{"version":2,"type":"language.change"}""", out _, out var rejection));
        Assert.Equal(ShellMessageRejection.InvalidPayload, rejection);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{"version":2,"type":"ui.ready",}""")]
    [InlineData("""{"version":1 /* c */,"type":"ui.ready"}""")]
    [InlineData("""{"version":2,"type":"ui.ready"} trailing""")]
    [InlineData("""{'version':1,'type':'ui.ready'}""")]
    public void RejectsMalformedJson(string raw)
    {
        Assert.False(ShellMessageParser.TryParse(raw, out _, out var rejection));
        Assert.Equal(ShellMessageRejection.MalformedJson, rejection);
    }

    [Fact]
    public void RejectsDeeplyNestedPayloadsAsMalformed()
    {
        var raw = """{"version":2,"type":"ui.ready","payload":{"a":{"b":{"c":{"d":1}}}}}""";

        Assert.False(ShellMessageParser.TryParse(raw, out _, out var rejection));
        Assert.Equal(ShellMessageRejection.MalformedJson, rejection);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"ui.ready\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void RejectsNonObjectMessages(string raw)
    {
        Assert.False(ShellMessageParser.TryParse(raw, out _, out var rejection));
        Assert.Equal(ShellMessageRejection.NotAnObject, rejection);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RejectsEmptyMessages(string? raw)
    {
        Assert.False(ShellMessageParser.TryParse(raw, out _, out var rejection));
        Assert.Equal(ShellMessageRejection.Empty, rejection);
    }

    [Fact]
    public void EnforcesThePayloadSizeLimitBeforeParsing()
    {
        var padding = new string(' ', ShellMessageContract.MaxInboundMessageLength);
        var oversized = Command("ui.ready") + padding;
        var atLimit = Command("ui.ready").PadRight(ShellMessageContract.MaxInboundMessageLength);

        Assert.False(ShellMessageParser.TryParse(oversized, out _, out var rejection));
        Assert.Equal(ShellMessageRejection.TooLarge, rejection);
        Assert.True(ShellMessageParser.TryParse(atLimit, out _, out _));
    }
}
