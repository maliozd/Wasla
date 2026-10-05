using System.Text.Json.Nodes;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// Regression tests for the incident in which the tray application's exit-time settings save (the window layout) ran
/// after its test had released the temporary data root and overwrote the machine's real settings file. The real tray
/// application runs with the classic window, so no WebView2 Runtime is needed. Outside an isolated root the paths
/// point at the run's <see cref="EscapedWriteCanary"/>, never at ProgramData, and an escaped write fails the test.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class TrayShutdownIsolationTests : IDisposable
{
    private const int StartLeft = -32000;
    private const int MovedLeft = -31000;

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
                Assert.Equal(MovedLeft, saved["Ui"]!["WindowLeft"]!.GetValue<int>());
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
        await _host.RunAsync(shell: null, available: false, (tray, _) =>
        {
            tray.ClassicWindowForTests.Location = new Point(MovedLeft, MovedLeft);
            return Task.CompletedTask;
        }, exit: TrayExit.DuringTeardown);

        // Reported done while the application still runs: the window layout is not saved yet.
        Assert.Equal(StartLeft, SavedWindowLeft());
        _exitSaveExpectedDuringTeardown = true;
    }

    [Fact]
    public async Task AFailingTest_StillExitsTheTrayInsideTheIsolatedRoot_BeforeTheFailureIsReported()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _host.RunAsync(shell: null, available: false, (tray, _) =>
            {
                tray.ClassicWindowForTests.Location = new Point(MovedLeft, MovedLeft);
                throw new InvalidOperationException("Deliberate failure inside the test body.");
            }));

        Assert.Equal("Deliberate failure inside the test body.", failure.Message);
        // The exit save already ran, inside the isolated root, before the failure reached the test.
        Assert.Equal(MovedLeft, SavedWindowLeft());
        EscapedWriteCanary.AssertNothingEscaped("after the failing tray test");
    }

    private static int? SavedWindowLeft() => new PrintBridgeSettingsStore().Load().Ui.WindowLeft;
}
