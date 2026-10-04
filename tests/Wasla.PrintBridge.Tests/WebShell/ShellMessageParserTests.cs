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
        Assert.True(ShellMessageParser.TryParse("""{"version":1,"type":"ui.ready"}""", out var command, out _));
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

        Assert.Equal(new ShellCommand(ShellCommandType.LanguageChange, culture), command);
    }

    [Fact]
    public void ContractListsExactlyTheParsedCommands()
    {
        Assert.Equal(
            ["ui.ready", "snapshot.request", "language.change", "classicWindow.open"],
            ShellMessageContract.CommandTypes);
        Assert.Equal(ShellMessageContract.CommandTypes.Count, Enum.GetValues<ShellCommandType>().Length);
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
    [InlineData("""{"version":1,"type":42}""", ShellMessageRejection.UnknownType)]
    [InlineData("""{"version":1}""", ShellMessageRejection.UnknownType)]
    [InlineData("""{"type":"ui.ready"}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":2,"type":"ui.ready"}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":"1","type":"ui.ready"}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":1.5,"type":"ui.ready"}""", ShellMessageRejection.UnsupportedVersion)]
    [InlineData("""{"version":1,"type":"ui.ready","method":"Exit"}""", ShellMessageRejection.UnexpectedProperty)]
    [InlineData("""{"version":1,"type":"ui.ready","Type":"x"}""", ShellMessageRejection.UnexpectedProperty)]
    [InlineData("""{"version":1,"type":"ui.ready","type":"classicWindow.open"}""", ShellMessageRejection.DuplicateProperty)]
    [InlineData("""{"version":1,"version":1,"type":"ui.ready"}""", ShellMessageRejection.DuplicateProperty)]
    [InlineData("""{"version":1,"type":"ui.ready","payload":[]}""", ShellMessageRejection.InvalidPayload)]
    [InlineData("""{"version":1,"type":"ui.ready","payload":"x"}""", ShellMessageRejection.InvalidPayload)]
    [InlineData("""{"version":1,"type":"ui.ready","payload":{"force":true}}""", ShellMessageRejection.InvalidPayload)]
    [InlineData("""{"version":1,"type":"classicWindow.open","payload":{"tab":"settings"}}""", ShellMessageRejection.InvalidPayload)]
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
        Assert.False(ShellMessageParser.TryParse("""{"version":1,"type":"language.change"}""", out _, out var rejection));
        Assert.Equal(ShellMessageRejection.InvalidPayload, rejection);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{"version":1,"type":"ui.ready",}""")]
    [InlineData("""{"version":1 /* c */,"type":"ui.ready"}""")]
    [InlineData("""{"version":1,"type":"ui.ready"} trailing""")]
    [InlineData("""{'version':1,'type':'ui.ready'}""")]
    public void RejectsMalformedJson(string raw)
    {
        Assert.False(ShellMessageParser.TryParse(raw, out _, out var rejection));
        Assert.Equal(ShellMessageRejection.MalformedJson, rejection);
    }

    [Fact]
    public void RejectsDeeplyNestedPayloadsAsMalformed()
    {
        var raw = """{"version":1,"type":"ui.ready","payload":{"a":{"b":{"c":{"d":1}}}}}""";

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
