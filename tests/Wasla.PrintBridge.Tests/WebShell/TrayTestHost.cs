using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>When the tray application exits, relative to the end of the test.</summary>
internal enum TrayExit
{
    /// <summary>On the UI thread before the test is reported done. Every normal test uses this.</summary>
    BeforeTheTestEnds,

    /// <summary>
    /// Only after the test was reported done and its teardown began: the ordering that once let a write made while the
    /// application exited reach the machine's real settings file. Used to prove the data root outlives every writer.
    /// </summary>
    DuringTeardown
}

/// <summary>
/// The real tray application (services, engine, WebView2 window or runtime notice) on its own STA message-loop thread,
/// with a private data root that tracks the thread, so teardown waits for the application to exit before the root is
/// released. The tray icon is hidden, every window opens off-screen, and a setup-link result that would be shown in a
/// message box is recorded in <see cref="SetupResultMessages"/> instead. No device token is configured unless a test
/// points the engine at a local fake server with a fake token, so by default nothing contacts a server and the
/// application starts as a first run.
/// </summary>
internal sealed class TrayTestHost(IsolatedDataRoot dataRoot)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>A failure while exiting with <see cref="TrayExit.DuringTeardown"/>, after the test was reported done.</summary>
    public Exception? TeardownFailure { get; private set; }

    /// <summary>Setup-link results the application would have shown in a message box.</summary>
    public ConcurrentQueue<string> SetupResultMessages { get; } = new();

    /// <param name="available">Whether the WebView2 Runtime counts as usable. True needs a real runtime (skipped otherwise).</param>
    public Task RunAsync(
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
                    // Every window opens off-screen; the tests check what opens, not where. These hooks run before a
                    // window is first shown, including the one a first run opens once the message loop runs.
                    tray.ShellFormCreatedForTests = form =>
                    {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-32000, -32000);
                        form.ShowInTaskbar = false;
                        form.FailStartupForTests = failShellStartup;
                    };
                    tray.RuntimeNoticeCreatedForTests = notice =>
                    {
                        notice.StartPosition = FormStartPosition.Manual;
                        notice.Location = new Point(-32000, -32000);
                        notice.ShowInTaskbar = false;
                    };
                    tray.SetupResultMessageForTests = SetupResultMessages.Enqueue;
                    // The application's own culture service, so the expected text follows the language it runs in.
                    var localizer = new PrintBridgeLocalizer(services.GetRequiredService<PrintBridgeCultureService>());
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
