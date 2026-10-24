using System.Text.Json.Nodes;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// Regression tests for the incident in which a settings save the tray application made while it exited ran after its
/// test had released the temporary data root and overwrote the machine's real settings file. The application no longer
/// saves anything on exit, so the save is made by the application's own UI thread as it exits (its message loop ending),
/// which is the latest point any of its components can write. The runtime notice stands in for the app window, so no
/// WebView2 Runtime is needed. Outside an isolated root the paths point at the run's <see cref="EscapedWriteCanary"/>,
/// never at ProgramData, and an escaped write fails the test.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class TrayShutdownIsolationTests : IDisposable
{
    private const string StartLanguage = "tr-TR";
    private const string ExitLanguage = "ru-RU";

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-tray-isolation-tests");
    private readonly CultureScope _cultureScope = new();
    private readonly TrayTestHost _host;
    private bool _exitSaveExpectedDuringTeardown;

    public TrayShutdownIsolationTests() => _host = new TrayTestHost(_dataRoot);

    public void Dispose()
    {
        try
        {
            // The shared teardown: wait for the application (whose exit save may only run now), release the root, and
            // fail if anything reached the canary instead.
            _dataRoot.Release();
            if (_exitSaveExpectedDuringTeardown)
            {
                if (_host.TeardownFailure is { } failure)
                    throw new InvalidOperationException("The tray application failed while exiting during teardown.", failure);

                // The late save is in the isolated folder, which is kept until Dispose.
                var saved = JsonNode.Parse(File.ReadAllText(_dataRoot.ConfigPath))!;
                Assert.Equal(ExitLanguage, saved["Ui"]!["Language"]!.GetValue<string>());
            }
        }
        finally
        {
            _dataRoot.Dispose();
            _cultureScope.Dispose();
        }
    }

    [Fact]
    public async Task AnExitSaveThatRunsAfterTheTestEnds_StaysInTheIsolatedRoot()
    {
        await _host.RunAsync(available: false, (tray, _) =>
        {
            SaveWhenTheApplicationExits(tray);
            return Task.CompletedTask;
        }, exit: TrayExit.DuringTeardown);

        // Reported done while the application still runs: nothing is saved yet.
        Assert.Equal(StartLanguage, SavedLanguage());
        _exitSaveExpectedDuringTeardown = true;
    }

    [Fact]
    public async Task AFailingTest_StillExitsTheTrayInsideTheIsolatedRoot_BeforeTheFailureIsReported()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _host.RunAsync(available: false, (tray, _) =>
            {
                SaveWhenTheApplicationExits(tray);
                throw new InvalidOperationException("Deliberate failure inside the test body.");
            }));

        Assert.Equal("Deliberate failure inside the test body.", failure.Message);
        // The exit save already ran, inside the isolated root, before the failure reached the test.
        Assert.Equal(ExitLanguage, SavedLanguage());
        EscapedWriteCanary.AssertNothingEscaped("after the failing tray test");
    }

    /// <summary>Saves a settings change on the application's UI thread when its message loop ends.</summary>
    private static void SaveWhenTheApplicationExits(TrayApplicationContext tray) =>
        tray.ThreadExit += (_, _) => new PrintBridgeSettingsStore().SaveLanguage(ExitLanguage);

    private static string SavedLanguage() => new PrintBridgeSettingsStore().Load().Ui.Language;
}
