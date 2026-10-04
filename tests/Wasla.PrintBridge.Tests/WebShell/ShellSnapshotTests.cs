using System.Text.Json;
using System.Text.Json.Nodes;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

[Collection(ProcessCultureCollection.Name)]
public sealed class ShellSnapshotTests : IDisposable
{
    private readonly CultureScope _cultureScope = new();

    public void Dispose() => _cultureScope.Dispose();

    public static TheoryData<BridgeServerConnectionStatus, PrintBridgeRuntimeIssueCode?, bool, string> ConnectionCases => new()
    {
        { BridgeServerConnectionStatus.Connected, null, true, ShellConnectionStates.Online },
        { BridgeServerConnectionStatus.Disconnected, null, true, ShellConnectionStates.Connecting },
        { BridgeServerConnectionStatus.Stopped, null, false, ShellConnectionStates.Stopped },
        { BridgeServerConnectionStatus.NotConfigured, null, false, ShellConnectionStates.NotConfigured },
        { BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssueCode.ServerUnreachable, true, ShellConnectionStates.Offline },
        { BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssueCode.SslError, true, ShellConnectionStates.Error },
        { BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssueCode.EndpointNotFound, true, ShellConnectionStates.Error },
        { BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssueCode.RequestFailed, true, ShellConnectionStates.Error },
        { BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssueCode.Unexpected, true, ShellConnectionStates.Error },
        { BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssueCode.ReconnectRequired, false, ShellConnectionStates.Error },
        { BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssueCode.DisabledByAdmin, true, ShellConnectionStates.Error },
        { BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssueCode.DuplicateInstallation, false, ShellConnectionStates.Error },
        // A blocking lifecycle issue wins even if the engine still reports no configuration.
        { BridgeServerConnectionStatus.NotConfigured, PrintBridgeRuntimeIssueCode.ReconnectRequired, false, ShellConnectionStates.Error }
    };

    [Theory]
    [MemberData(nameof(ConnectionCases))]
    public void ConnectionState_MapsEngineTruth(
        BridgeServerConnectionStatus connection,
        PrintBridgeRuntimeIssueCode? issueCode,
        bool running,
        string expected)
    {
        var issue = issueCode is null ? null : new PrintBridgeRuntimeIssue(issueCode.Value);

        Assert.Equal(expected, ShellSnapshotFactory.MapConnectionState(Status(connection, issue, running)));
    }

    [Fact]
    public void EveryMappedState_IsOneThePageKnows()
    {
        foreach (var row in ConnectionCases)
            Assert.Contains(row.Data.Item4, ShellConnectionStates.All);
    }

    [Fact]
    public void BlockingIssue_UsesTheExistingLocalizedTitleAndDetail()
    {
        var culture = Culture();
        var snapshot = Factory(culture).Create(Status(
            BridgeServerConnectionStatus.Error,
            new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DisabledByAdmin)));
        var localizer = new PrintBridgeLocalizer(culture);

        Assert.Equal(ShellConnectionStates.Error, snapshot.Connection.State);
        Assert.Equal(localizer["RuntimeIssue.DisabledByAdmin.Title"], snapshot.Connection.Label);
        Assert.Equal(localizer["RuntimeIssue.DisabledByAdmin.Detail"], snapshot.Connection.Detail);
    }

    [Fact]
    public void UnexpectedErrors_NeverExposeRawExceptionText()
    {
        const string raw = "System.Net.Http.HttpRequestException: secret-host.internal:4431 refused, X-PrintBridge-Token=abc";
        var culture = Culture();
        var snapshot = Factory(culture).Create(Status(BridgeServerConnectionStatus.Error, PrintBridgeRuntimeIssue.FromRaw(raw)));

        var json = ShellMessageSerializer.SerializePayload(snapshot);

        Assert.Equal(new PrintBridgeLocalizer(culture)["Shell.Detail.UnexpectedError"], snapshot.Connection.Detail);
        Assert.DoesNotContain("secret-host", json, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializedSnapshot_ContainsNoTokenServerUrlOrInstallationIdentity()
    {
        var culture = Culture();
        var snapshot = Factory(culture).Create(Status());

        var json = ShellMessageSerializer.SerializeSnapshotMessage(snapshot, 7);

        Assert.DoesNotContain(SentinelServerUrl, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sentinel-tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serverUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QA-MACHINE", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotModel_HasNoCredentialOrTenantFields()
    {
        var forbidden = new[] { "token", "secret", "password", "serverurl", "url", "installation", "tenant", "credential" };
        var types = new[]
        {
            typeof(ShellSnapshot), typeof(ShellConnectionView), typeof(ShellDeviceView), typeof(ShellPrinterView),
            typeof(ShellActivityView), typeof(ShellJobView), typeof(ShellLanguageOption), typeof(ShellSnapshotMessage)
        };

        foreach (var property in types.SelectMany(t => t.GetProperties()))
        {
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, property.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Envelope_UsesTheVersionedCamelCaseContract()
    {
        var snapshot = Factory(Culture()).Create(Status());

        var root = JsonNode.Parse(ShellMessageSerializer.SerializeSnapshotMessage(snapshot, 3))!.AsObject();

        Assert.Equal(["version", "type", "sequence", "payload"], root.Select(p => p.Key));
        Assert.Equal(1, root["version"]!.GetValue<int>());
        Assert.Equal("snapshot.updated", root["type"]!.GetValue<string>());
        Assert.Equal(3, root["sequence"]!.GetValue<long>());
    }

    [Fact]
    public void SerializedShape_MatchesTheSharedFixtureUsedByThePageTests()
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "tests", "Wasla.PrintBridge.Tests", "WebShell", "shell-snapshot.fixture.json");
        var fixture = JsonNode.Parse(File.ReadAllText(fixturePath))!;
        var job = new LocalPrintJobRecord
        {
            JobId = Guid.NewGuid(),
            OrderDisplay = "GTR-1001",
            JobType = "Receipt",
            Status = LocalPrintJobStatus.Printed,
            CreatedAtUtc = DateTime.UtcNow,
            PrintedAtUtc = DateTime.UtcNow
        };
        var snapshot = Factory(Culture()).Create(Status(jobs: [job], dryRun: true, lastPrintUtc: DateTime.UtcNow));
        var actual = JsonNode.Parse(ShellMessageSerializer.SerializeSnapshotMessage(snapshot, 1))!;

        Assert.Equal(Shape(fixture), Shape(actual));
    }

    [Fact]
    public void Strings_CarryEveryPageLabelWithoutPlaceholders()
    {
        var snapshot = Factory(Culture(SupportedCultures.Russian)).Create(Status());

        Assert.Equal(ShellSnapshotFactory.StringKeys.Order(), snapshot.Strings.Keys.Order());
        foreach (var (key, value) in snapshot.Strings)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), key);
            Assert.NotEqual(key, value);
            Assert.DoesNotMatch(@"\{\d+\}", value);
        }
    }

    [Theory]
    [InlineData(SupportedCultures.Turkish, "ltr")]
    [InlineData(SupportedCultures.English, "ltr")]
    [InlineData(SupportedCultures.Arabic, "rtl")]
    [InlineData(SupportedCultures.Russian, "ltr")]
    public void Culture_AndDirection_FollowTheHostCulture(string culture, string direction)
    {
        var snapshot = Factory(Culture(culture)).Create(Status());

        Assert.Equal(culture, snapshot.Culture);
        Assert.Equal(direction, snapshot.Direction);
        Assert.Equal(SupportedCultures.All, snapshot.Languages.Select(l => l.Culture));
    }

    [Fact]
    public void LastJob_IsTheMostRecentSessionJobWithLocalizedStatus()
    {
        var culture = Culture(SupportedCultures.English);
        var newest = new LocalPrintJobRecord { JobId = Guid.NewGuid(), OrderDisplay = "NEW-2", Status = LocalPrintJobStatus.Failed, CreatedAtUtc = DateTime.UtcNow };
        var older = new LocalPrintJobRecord { JobId = Guid.NewGuid(), OrderDisplay = "OLD-1", Status = LocalPrintJobStatus.Printed, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5) };

        var snapshot = Factory(culture).Create(Status(jobs: [newest, older]));

        Assert.Equal("NEW-2", snapshot.LastJob!.Order);
        Assert.Equal("failed", snapshot.LastJob.Status);
        Assert.Equal(new PrintBridgeLocalizer(culture).GetJobStatusBadge(LocalPrintJobStatus.Failed), snapshot.LastJob.StatusLabel);
        Assert.Null(Factory(culture).Create(Status()).LastJob);
    }

    private static string Shape(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.Select(p => p.Key + ":" + Shape(p.Value))) + "}",
        JsonArray array => "[" + (array.Count > 0 ? Shape(array[0]) : string.Empty) + "]",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "bool",
            _ => "null"
        },
        null => "null",
        _ => "?"
    };
}
