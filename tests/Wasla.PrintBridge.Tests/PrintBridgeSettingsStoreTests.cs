using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-135: the settings file and the in-memory settings hold exactly the Wasla connection, the Print Bridge settings and
/// the UI settings, and the bridge reads the tenant's name from the server's health response. Every path points at a
/// temporary data root; tokens are fake.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeSettingsStoreTests : IDisposable
{
    private const string FakeToken = "test-token-not-a-real-credential";
    private const string ServerUrl = "https://panel.wasla.test";

    private static readonly string[] Sections = ["Wasla", "PrintBridge", "Ui"];

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-settings-store-tests");

    public void Dispose() => _dataRoot.Dispose();

    [Fact]
    public void Saving_WritesExactlyTheWaslaPrintBridgeAndUiSections()
    {
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.Wasla.ServerUrl = ServerUrl;
        document.Wasla.AgentToken = FakeToken;
        document.Ui.Language = "en-US";

        store.Save(document);

        var root = SavedRoot();
        Assert.Equal(Sections, root.Select(p => p.Key));
        Assert.Equal(ServerUrl, (string?)root["Wasla"]!["ServerUrl"]);
        Assert.Equal(FakeToken, (string?)root["Wasla"]!["AgentToken"]);
        Assert.Equal("en-US", (string?)root["Ui"]!["Language"]);
    }

    [Fact]
    public void Loading_RestoresTheServerUrlAndTokenFromTheWaslaSection()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        File.WriteAllText(PrintBridgePaths.ProgramDataConfigPath, $$"""
            {
              "Wasla": { "ServerUrl": " {{ServerUrl}}/ ", "AgentToken": " {{FakeToken}} " },
              "PrintBridge": { "PrinterName": "POS-58", "DryRun": true },
              "Ui": { "Language": "ar-SA" }
            }
            """);

        var document = new PrintBridgeSettingsStore().Load();

        Assert.Equal(ServerUrl, document.Wasla.ServerUrl);
        Assert.Equal(FakeToken, document.Wasla.AgentToken);
        Assert.Equal("POS-58", document.PrintBridge.PrinterName);
        Assert.Equal("ar-SA", document.Ui.Language);
    }

    [Fact]
    public void FirstRun_SeedsTheSettingsFromTheShippedSample_WithExactlyTheApprovedSections()
    {
        // The sample ships next to Wasla.PrintBridge.exe and is copied next to this test assembly.
        var sample = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.Equal(Sections, ParseRoot(File.ReadAllText(sample)).Select(p => p.Key));
        Assert.False(File.Exists(PrintBridgePaths.ProgramDataConfigPath));

        var store = new PrintBridgeSettingsStore();
        store.SeedProgramDataConfigIfMissing();

        Assert.Equal(Sections, SavedRoot().Select(p => p.Key));
        var seeded = store.Load();
        Assert.Equal(WaslaOptions.DefaultServerUrl, seeded.Wasla.ServerUrl);
        Assert.Equal(string.Empty, seeded.Wasla.AgentToken);
        // Values that differ from the built-in defaults prove the sample, not the defaults, was used.
        Assert.False(seeded.PrintBridge.DryRun);
        Assert.Equal(
            (5, 1, 15, 3),
            (seeded.PrintBridge.IdlePollIntervalSeconds, seeded.PrintBridge.BusyPollIntervalSeconds,
                seeded.PrintBridge.ErrorPollIntervalSeconds, seeded.PrintBridge.MaxJobsPerPoll));
        Assert.True(Guid.TryParse(seeded.PrintBridge.InstallationId, out _));
    }

    [Fact]
    public void SettingsDocumentAndHolder_ExposeExactlyTheConnectionBridgeAndUiSettings()
    {
        Assert.Equal(
            [("Wasla", typeof(WaslaOptions)), ("PrintBridge", typeof(PrintBridgeOptions)), ("Ui", typeof(UiOptions))],
            PublicProperties(typeof(PrintBridgeSettingsStore.AppSettingsDocument)));
        Assert.Equal(
            ["Wasla", "PrintBridge", "Ui"],
            [WaslaOptions.SectionName, PrintBridgeOptions.SectionName, UiOptions.SectionName]);

        Assert.Equal(
            [("Connection", typeof(WaslaOptions)), ("Bridge", typeof(PrintBridgeOptions)), ("Ui", typeof(UiOptions))],
            PublicProperties(typeof(PrintBridgeSettingsHolder)));

        var snapshot = typeof(PrintBridgeSettingsHolder).GetMethod(nameof(PrintBridgeSettingsHolder.Snapshot))!;
        Assert.Equal(typeof((WaslaOptions, PrintBridgeOptions, UiOptions)), snapshot.ReturnType);
        Assert.Equal(
            ["Connection", "Bridge", "Ui"],
            snapshot.ReturnParameter.GetCustomAttribute<TupleElementNamesAttribute>()!.TransformNames);

        var replace = typeof(PrintBridgeSettingsHolder).GetMethod(nameof(PrintBridgeSettingsHolder.Replace))!;
        Assert.Equal(["connection", "bridge", "ui"], replace.GetParameters().Select(p => p.Name));
    }

    [Fact]
    public void TheLogFile_IsTheDailyWaslaPrintBridgeLog_InTheLogFolder()
    {
        Assert.Equal(PrintBridgePaths.ProgramDataLogDirectory, Path.GetDirectoryName(PrintBridgeAppServices.LogFilePath));
        Assert.Equal("wasla-print-bridge-.log", Path.GetFileName(PrintBridgeAppServices.LogFilePath));
    }

    [Fact]
    public async Task HealthCheck_ReadsTheTenantNameThatTheServerSends()
    {
        // The server's serialized health response (its property set is checked in Wasla.UnitTests).
        const string json = """
            {"success":true,"tenantName":"Lokanta Wasla","deviceName":"Kasa 1","serverTimeUtc":"2026-10-19T12:00:00Z","machineName":"KASA-PC","installationId":"4b6f0c8e-1f8a-4f5e-9d9e-0f3a9c000001"}
            """;
        using var handler = new HealthHandler(json);
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new WaslaPrintBridgeClient(
            http,
            new PrintBridgeSettingsHolder(),
            new PrintBridgeSetupHttpClientFactory(handler),
            NullLogger<WaslaPrintBridgeClient>.Instance,
            "1.0.0.0");

        var health = await client.TestHealthAsync(
            new WaslaOptions { ServerUrl = ServerUrl, AgentToken = FakeToken },
            TestContext.Current.CancellationToken);

        Assert.Equal("Lokanta Wasla", health.TenantName);
        Assert.Equal("Kasa 1", health.DeviceName);
        Assert.Equal("KASA-PC", health.MachineName);
        Assert.Equal(
            ["TenantName", "DeviceName", "ServerTimeUtc", "MachineName"],
            typeof(WaslaPrintBridgeClient.PrintBridgeHealthResult).GetProperties().Select(p => p.Name));
    }

    private static JsonObject SavedRoot() => ParseRoot(File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath));

    private static JsonObject ParseRoot(string json) => JsonNode.Parse(json)!.AsObject();

    private static IEnumerable<(string Name, Type Type)> PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.MetadataToken)
            .Select(p => (p.Name, p.PropertyType));

    private sealed class HealthHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal($"{ServerUrl}/api/print-bridge/health", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
