using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// The guards that keep the test run away from the machine's real Print Bridge files: outside every isolated root the
/// paths point at the run's <see cref="EscapedWriteCanary"/>, an escaped write there is reported, and a root is not
/// released while a thread that may still write is running. Nothing here touches ProgramData.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class TestIsolationGuardTests
{
    [Fact]
    public void WithoutATestRoot_EveryPathPointsAtTheCanary_NeverAtProgramData()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.Equal(EscapedWriteCanary.Root, PrintBridgePaths.ProgramDataRoot);
        foreach (var path in new[]
                 {
                     PrintBridgePaths.ProgramDataConfigPath,
                     PrintBridgePaths.ProgramDataHistoryPath,
                     PrintBridgePaths.ProgramDataLogDirectory,
                     ShellPaths.UserDataDirectory
                 })
        {
            Assert.StartsWith(EscapedWriteCanary.Root, path, StringComparison.OrdinalIgnoreCase);
            Assert.False(path.StartsWith(programData, StringComparison.OrdinalIgnoreCase), "A test path resolved to ProgramData.");
        }
    }

    [Fact]
    public void AWriteOutsideEveryIsolatedRoot_LandsInTheCanary_AndIsReported()
    {
        EscapedWriteCanary.AssertNothingEscaped("before this test");
        try
        {
            // Exactly what a shutdown save does after its test has released the root.
            var store = new PrintBridgeSettingsStore();
            store.Save(store.Load());

            Assert.True(File.Exists(Path.Combine(EscapedWriteCanary.Root, "appsettings.json")));
            var reported = Assert.Throws<InvalidOperationException>(() => EscapedWriteCanary.AssertNothingEscaped("in this test"));
            Assert.Contains("appsettings.json", reported.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(EscapedWriteCanary.Root, recursive: true);
        }

        EscapedWriteCanary.AssertNothingEscaped("after this test");
    }

    [Fact]
    public void ARootIsNotReleasedWhileATrackedThreadRuns_AndItsLateWriteStaysInside()
    {
        using var release = new ManualResetEventSlim();
        var root = new IsolatedDataRoot("wasla-pb-isolation-tests", threadTimeout: TimeSpan.FromMilliseconds(200));
        var writer = new Thread(() =>
        {
            release.Wait();
            var store = new PrintBridgeSettingsStore();
            var document = store.Load();
            document.Ui.WindowLeft = 1234;
            store.Save(document);
        })
        {
            IsBackground = true
        };
        root.Track(writer);
        writer.Start();
        try
        {
            // The writer is blocked, so it cannot end within the timeout: teardown fails and keeps the redirect.
            Assert.Throws<InvalidOperationException>(root.Dispose);
            Assert.Equal(root.Path, PrintBridgePaths.ProgramDataRoot);

            release.Set();
            Assert.True(writer.Join(TimeSpan.FromSeconds(30)));
            Assert.Equal(1234, new PrintBridgeSettingsStore().Load().Ui.WindowLeft);
            Assert.True(File.Exists(root.ConfigPath));

            root.Dispose();
            Assert.Equal(EscapedWriteCanary.Root, PrintBridgePaths.ProgramDataRoot);
            Assert.False(Directory.Exists(root.Path));
            EscapedWriteCanary.AssertNothingEscaped("after the late writer ended");
        }
        finally
        {
            // Never leave the writer blocked, even when an assertion above failed.
            release.Set();
            writer.Join(TimeSpan.FromSeconds(30));
        }
    }
}
