using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Updates;

namespace Wasla.PrintBridge.Tests.Updates;

/// <summary>
/// WAS-63 contracts that need no service: release versions as Windows Installer compares them, the release description the
/// server sends, the durable update record, and the setup checks that do not depend on a signature.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeReleaseTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-release-tests");

    public void Dispose() => _dataRoot.Dispose();

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("255.255.65535", true)]
    [InlineData("0.0.1", true)]
    [InlineData("256.0.0", false)]
    [InlineData("1.256.0", false)]
    [InlineData("1.0.65536", false)]
    [InlineData("1.0", false)]
    [InlineData("1.0.0.0", false)]
    [InlineData("1.0.0-beta", false)]
    [InlineData(" 1.0.0", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AReleaseVersion_IsExactlyThreeWindowsInstallerParts(string? text, bool valid)
    {
        Assert.Equal(valid, PrintBridgeReleaseVersion.TryParse(text, out var version));
        if (valid)
            Assert.Equal(text, PrintBridgeReleaseVersion.Format(version));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("1.0.1.0", "1.0.1")]
    [InlineData("8.0.31+1219a42122cf5190c7f512850557e38c422430dc", "8.0.31")]
    [InlineData("10.0.12 @Commit: 95017c711e6a", "10.0.12")]
    [InlineData("8,0,3126,42015 @Commit: 1219a421", null)]
    [InlineData("1.0", null)]
    [InlineData("1.0.70000", null)]
    [InlineData(null, null)]
    public void AVersionResource_IsReadAsItsReleaseVersion(string? resource, string? expected)
    {
        var version = PrintBridgeReleaseVersion.FromResource(resource);
        Assert.Equal(expected, version is null ? null : PrintBridgeReleaseVersion.Format(version));
    }

    [Fact]
    public void ADescribedRelease_HasEveryFieldInRange()
    {
        var release = PrintBridgeRelease.TryCreate(1, "1.0.1", "Wasla.PrintBridge-Setup-x64.exe", 1234, Hash);
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 0, 1), release.Version);

        Assert.Null(PrintBridgeRelease.TryCreate(2, "1.0.1", "Wasla.PrintBridge-Setup-x64.exe", 1234, Hash));
        Assert.Null(PrintBridgeRelease.TryCreate(1, "1.0.1.0", "Wasla.PrintBridge-Setup-x64.exe", 1234, Hash));
        Assert.Null(PrintBridgeRelease.TryCreate(1, "1.0.1", "..\\Wasla.PrintBridge-Setup-x64.exe", 1234, Hash));
        Assert.Null(PrintBridgeRelease.TryCreate(1, "1.0.1", "Wasla.PrintBridge-x64.msi", 1234, Hash));
        Assert.Null(PrintBridgeRelease.TryCreate(1, "1.0.1", null, 1234, Hash));
        Assert.Null(PrintBridgeRelease.TryCreate(1, "1.0.1", "Wasla.PrintBridge-Setup-x64.exe", 0, Hash));
        Assert.Null(PrintBridgeRelease.TryCreate(1, "1.0.1", "Wasla.PrintBridge-Setup-x64.exe", PrintBridgeRelease.MaxSetupBytes + 1, Hash));
        Assert.Null(PrintBridgeRelease.TryCreate(1, "1.0.1", "Wasla.PrintBridge-Setup-x64.exe", 1234, Hash.ToUpperInvariant()));
        Assert.Null(PrintBridgeRelease.TryCreate(1, "1.0.1", "Wasla.PrintBridge-Setup-x64.exe", 1234, Hash[..63]));
    }

    [Fact]
    public void TheUpdateRecord_IsWrittenAtomically_WithNamesForItsStages_AndNoPathOrSecret()
    {
        var store = new PrintBridgeUpdateStateStore(NullLogger<PrintBridgeUpdateStateStore>.Instance);
        var record = new PrintBridgeUpdateRecord(
            PrintBridgeUpdateRecord.CurrentSchemaVersion, Guid.NewGuid(), "1.0.0", "1.0.1", 1234, Hash,
            PrintBridgeUpdateStage.WaitingForPrinting, DateTime.UtcNow, null, null, null);

        store.Save(record);

        Assert.Equal(record, store.Load());
        var json = File.ReadAllText(PrintBridgePaths.UpdateStatePath);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("waitingForPrinting", document.RootElement.GetProperty("stage").GetString());
        Assert.DoesNotContain(_dataRoot.Path.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(PrintBridgePaths.UpdatesDirectory, "*.tmp"));

        var failed = record.Finish(PrintBridgeUpdateStage.Failed, PrintBridgeUpdateFailure.HashMismatch);
        store.Save(failed);
        using var failedDocument = JsonDocument.Parse(File.ReadAllText(PrintBridgePaths.UpdateStatePath));
        Assert.Equal("hashMismatch", failedDocument.RootElement.GetProperty("failure").GetString());
        Assert.True(store.Load()!.IsFinished);
    }

    [Fact]
    public async Task ARecordInUseByAnotherProcess_IsReadOnceFree_AndNeverSetAside()
    {
        // In use is not damaged: an antivirus scan or a support tool may hold the file for a moment.
        var store = new PrintBridgeUpdateStateStore(NullLogger<PrintBridgeUpdateStateStore>.Instance);
        var record = new PrintBridgeUpdateRecord(
            PrintBridgeUpdateRecord.CurrentSchemaVersion, Guid.NewGuid(), "1.0.0", "1.0.1", 1234, Hash,
            PrintBridgeUpdateStage.Ready, DateTime.UtcNow, DateTime.UtcNow, null, null);
        store.Save(record);

        // Held without sharing for less than the reader waits: it is read once released.
        var held = new FileStream(PrintBridgePaths.UpdateStatePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);
        Assert.Equal(record, store.Load());
        await release;

        // Held for longer: not read, and left exactly as it is.
        using (new FileStream(PrintBridgePaths.UpdateStatePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(store.Load());
        Assert.Equal(record, store.Load());
        Assert.Empty(Directory.GetFiles(PrintBridgePaths.UpdatesDirectory, "update-state.json.corrupt-*"));

        // Reading, as the store reads, never keeps the service from replacing the record.
        var next = record.Finish(PrintBridgeUpdateStage.Succeeded);
        using (new FileStream(PrintBridgePaths.UpdateStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            store.Save(next);
        Assert.Equal(next, store.Load());
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "attemptId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "fromVersion": "1.0.0", "toVersion": "1.0.1", "stage": "ready", "startedAtUtc": "2026-11-14T10:00:00Z" }""")]
    [InlineData("""{ "schemaVersion": 1, "attemptId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "fromVersion": "1.0.0", "toVersion": "..\\x", "stage": "ready", "startedAtUtc": "2026-11-14T10:00:00Z" }""")]
    [InlineData("""{ "schemaVersion": 1, "attemptId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "fromVersion": "1.0.0", "toVersion": "1.0.1", "stage": 4, "startedAtUtc": "2026-11-14T10:00:00Z" }""")]
    [InlineData("not json")]
    public void AnUnknownOrDamagedRecord_IsSetAside_NotTrusted(string content)
    {
        Directory.CreateDirectory(PrintBridgePaths.UpdatesDirectory);
        File.WriteAllText(PrintBridgePaths.UpdateStatePath, content);

        Assert.Null(new PrintBridgeUpdateStateStore(NullLogger<PrintBridgeUpdateStateStore>.Instance).Load());
        Assert.False(File.Exists(PrintBridgePaths.UpdateStatePath));
        Assert.Single(Directory.GetFiles(PrintBridgePaths.UpdatesDirectory, "update-state.json.corrupt-*"));
    }

    [Fact]
    public void TheStagedSetup_IsNamedByItsVersion_InTheUpdatesFolder()
    {
        var staged = PrintBridgeUpdateStateStore.StagedSetupPath(new Version(1, 0, 1));

        Assert.Equal(Path.Combine(_dataRoot.Path, "updates", "Wasla.PrintBridge-Setup-x64-1.0.1.exe"), staged);
        Assert.Equal(staged + ".partial", PrintBridgeUpdateStateStore.PartialSetupPath(new Version(1, 0, 1)));
    }

    [Fact]
    public void ASetupOfTheWrongSizeOrHash_IsRefusedBeforeItsSignatureIsRead()
    {
        var path = Path.Combine(_dataRoot.Path, "setup.exe");
        File.WriteAllBytes(path, [1, 2, 3]);
        var publisher = new PrintBridgePublisher(new System.Security.Cryptography.X509Certificates.X500DistinguishedName("CN=Test"));
        var installed = new Version(1, 0, 0);

        Assert.Equal(PrintBridgeSetupVerification.Missing,
            PrintBridgeSetupVerifier.Verify(path + ".missing", new PrintBridgeRelease(new Version(1, 0, 1), "s.exe", 3, Hash), publisher, installed));
        Assert.Equal(PrintBridgeSetupVerification.SizeMismatch,
            PrintBridgeSetupVerifier.Verify(path, new PrintBridgeRelease(new Version(1, 0, 1), "s.exe", 4, Hash), publisher, installed));
        Assert.Equal(PrintBridgeSetupVerification.HashMismatch,
            PrintBridgeSetupVerifier.Verify(path, new PrintBridgeRelease(new Version(1, 0, 1), "s.exe", 3, Hash), publisher, installed));

        // The size and hash the server described, and no signature: a hash is not proof of who made the file.
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Assert.Equal(PrintBridgeSetupVerification.NotSigned,
            PrintBridgeSetupVerifier.Verify(path, new PrintBridgeRelease(new Version(1, 0, 1), "s.exe", 3, sha), publisher, installed));
    }

    [Fact]
    public void ADevelopmentBuild_HasNoPublisher_SoItCannotUpdateItself()
    {
        // This test host is not Authenticode-signed by Wasla; no publisher means no update is ever accepted.
        Assert.Equal(PrintBridgeSignatureStatus.NotSigned, PrintBridgeAuthenticode.Verify(typeof(PrintBridgeReleaseTests).Assembly.Location, out var publisher));
        Assert.Null(publisher);

        // The service reports the version of its own assembly, which is the one release version of the build.
        Assert.Equal(PrintBridgeReleaseVersion.Of(typeof(PrintBridgeServiceBackend).Assembly), PrintBridgeReleaseVersion.Of(typeof(PrintBridgeReleaseVersion).Assembly));
    }
}
