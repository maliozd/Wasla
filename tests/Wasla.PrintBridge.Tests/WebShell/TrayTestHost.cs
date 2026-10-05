using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>When the tray application exits; exiting saves its settings (the window layout).</summary>
internal enum TrayExit
{
    /// <summary>On the UI thread before the test is reported done. Every normal test uses this.</summary>
    BeforeTheTestEnds,

    /// <summary>
    /// Only after the test was reported done and its teardown began: the ordering that once let the exit save reach
    /// the machine's real settings file. Used to prove the data root outlives every writer.
    /// </summary>
    DuringTeardown
}

/// <summary>
/// The real tray application (services, engine, classic window, WebView2 window) on its own STA message-loop thread,
/// with a private data root that tracks the thread, so teardown waits for the application to exit before the root is
/// released. The tray icon is hidden and both windows open off-screen. No device token is configured unless a test
/// points the engine at a local fake server with a fake token, so by default nothing contacts a server.
/// </summary>
internal sealed class TrayTestHost(IsolatedDataRoot dataRoot)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>A failure while exiting with <see cref="TrayExit.DuringTeardown"/>, after the test was reported done.</summary>
    public Exception? TeardownFailure { get; private set; }

    public Task RunAsync(
        string? shell,
        bool available,
        Func<TrayApplicationContext, PrintBridgeLocalizer, Task> body,
        bool failShellStartup = false,
        TrayExit exit = TrayExit.BeforeTheTestEnds,
        Action<PrintBridgeSettingsStore.AppSettingsDocument>? configure = null)
    {
        if (available && !new WebView2RuntimeProbe().Probe().IsAvailable)
            Assert.Skip("No usable WebView2 Runtime is installed on this machine.");

        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.OrderHub.AgentToken = string.Empty;
        document.PrintBridge.DryRun = true;
        document.Ui.Language = SupportedCultures.Turkish;
        document.Ui.Shell = shell;
        // The classic window restores its saved position; keep it off-screen.
        document.Ui.WindowLeft = -32000;
        document.Ui.WindowTop = -32000;
        // A test that needs a listening engine points it at a local fake server with a fake token here.
        configure?.Invoke(document);
        store.Save(document);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var loop = new ApplicationContext();
            context.Post(async _ =>
            {
                TrayApplicationContext? tray = null;
                Exception? failure = null;
                try
                {
                    var services = PrintBridgeAppServices.Build();
                    tray = new TrayApplicationContext(services, new FixedProbe(available));
                    // No icon or balloon on the desktop; the menu is driven directly.
                    tray.TrayIconForTests.Visible = false;
                    // Both windows open off-screen; the tests check what opens, not where.
                    tray.ShellFormCreatedForTests = form =>
                    {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-32000, -32000);
                        form.ShowInTaskbar = false;
                        form.FailStartupForTests = failShellStartup;
                    };
                    tray.ClassicWindowForTests.StartPosition = FormStartPosition.Manual;
                    tray.ClassicWindowForTests.Location = new Point(-32000, -32000);
                    var localizer = new PrintBridgeLocalizer(new PrintBridgeCultureService());
                    await body(tray, localizer);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                if (exit == TrayExit.DuringTeardown)
                {
                    Report(completion, failure);
                    failure = null;
                    // Bounded, so a test that is never torn down cannot hang the run.
                    dataRoot.TeardownStarted.Wait(Timeout + Timeout);
                }

                // Exiting writes settings; normally it finishes before the test is reported done.
                try
                {
                    // Exit is queued on this thread and awaited, as from the tray menu; every request joins one shutdown.
                    if (tray is not null)
                        await tray.ExitForTests().WaitAsync(Timeout);
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                loop.ExitThread();
                if (exit == TrayExit.BeforeTheTestEnds)
                    Report(completion, failure);
                else
                    TeardownFailure = failure;
            }, null);
            System.Windows.Forms.Application.Run(loop);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        dataRoot.Track(thread);
        thread.Start();
        return completion.Task.WaitAsync(Timeout + Timeout);
    }

    private static void Report(TaskCompletionSource completion, Exception? failure)
    {
        if (failure is null)
            completion.TrySetResult();
        else
            completion.TrySetException(failure);
    }

    private sealed class FixedProbe(bool available) : IWebView2RuntimeProbe
    {
        public WebView2RuntimeAvailability Probe() =>
            available ? new WebView2RuntimeProbe().Probe() : new WebView2RuntimeAvailability(false, null);
    }
}
