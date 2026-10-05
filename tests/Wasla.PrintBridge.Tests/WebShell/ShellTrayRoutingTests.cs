using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// The real tray application (services, engine, classic window, WebView2 window) on a temporary data root, driven
/// through its tray menu. With <c>Ui.Shell = WebView2</c> every normal entry opens the one WebView2 window on the
/// matching tab; the classic window opens only from its explicit fallback entry or when WebView2 cannot run.
/// No device token is configured, so nothing contacts a server.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
[Trait("Category", "WebView2Runtime")]
public sealed class ShellTrayRoutingTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wasla-pb-tray-tests", Guid.NewGuid().ToString("N"));
    private readonly IDisposable _rootScope;
    private readonly CultureScope _cultureScope = new();
    private Thread? _uiThread;

    public ShellTrayRoutingTests()
    {
        Directory.CreateDirectory(_root);
        _rootScope = PrintBridgePaths.UseRootForTests(_root);
    }

    public void Dispose()
    {
        // The tray application saves settings (window layout) when it exits. The redirect to the temporary root
        // must outlive that thread, or the save would reach the machine's real settings file.
        if (_uiThread is { } thread && !thread.Join(TimeSpan.FromMinutes(2)))
            throw new InvalidOperationException("The tray test UI thread did not finish; the test root is kept to protect the real settings.");

        _rootScope.Dispose();
        _cultureScope.Dispose();
        for (var attempt = 0; attempt < 10 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }

    [Fact]
    public Task TrayHistoryAndSettings_OpenTheWebView2Tabs_InOneWindow_AndNeverTheClassicWindow() =>
        RunTrayAsync(ShellSelection.WebView2Value, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.PrintHistory"]);
            var shell = await WaitForAsync(() => tray.ShellFormForTests is { Visible: true } form ? form : null);
            await shell.InitializationForTests!.WaitAsync(Timeout);
            var core = shell.CoreWebView2ForTests!;
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('tab-history').getAttribute('aria-selected')") == "true");

            Click(tray, localizer["Tray.Settings"]);
            await WaitUntilAsync(async () => await EvalAsync(core, "document.getElementById('tab-settings').getAttribute('aria-selected')") == "true");
            Assert.Same(shell, tray.ShellFormForTests);

            shell.Hide();
            Click(tray, localizer["Tray.Open"]);
            await WaitUntilAsync(() => Task.FromResult(shell.Visible));
            Assert.Same(shell, tray.ShellFormForTests);

            Assert.Single(System.Windows.Forms.Application.OpenForms.OfType<PrintBridgeShellForm>());
            Assert.False(tray.ClassicWindowForTests.Visible);

            // The classic window is still one deliberate click away, as a labelled fallback.
            var fallback = Item(tray, localizer["Tray.OpenClassicFallback"]);
            OpenMenu(tray);
            Assert.True(fallback.Available);
            fallback.PerformClick();
            Assert.True(tray.ClassicWindowForTests.Visible);
        });

    [Fact]
    public Task MissingWebView2Runtime_TrayHistoryOpensTheClassicHistoryTab() =>
        RunTrayAsync(ShellSelection.WebView2Value, available: false, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.PrintHistory"]);

            Assert.Null(tray.ShellFormForTests);
            Assert.True(tray.ClassicWindowForTests.Visible);
            Assert.Equal(localizer["Tab.PrintHistory"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Equal("Shell.Fallback.RuntimeMissing", tray.FallbackNoticeForTests);
            OpenMenu(tray);
            Assert.False(Item(tray, localizer["Tray.OpenClassicFallback"]).Available);
            await Task.CompletedTask;
        });

    [Fact]
    public Task WebView2StartupFailure_FallsBackToTheClassicTabThatWasAskedFor() =>
        RunTrayAsync(ShellSelection.WebView2Value, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Settings"]);

            await WaitUntilAsync(() => Task.FromResult(tray.ClassicWindowForTests.Visible));
            Assert.Equal(localizer["Tab.Settings"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Equal("Shell.Fallback.StartFailed", tray.FallbackNoticeForTests);
            await WaitUntilAsync(() => Task.FromResult(tray.ShellFormForTests is null));

            // For the rest of the session the tray opens the classic window directly.
            tray.ClassicWindowForTests.Hide();
            Click(tray, localizer["Tray.PrintHistory"]);
            Assert.True(tray.ClassicWindowForTests.Visible);
            Assert.Equal(localizer["Tab.PrintHistory"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            Assert.Null(tray.ShellFormForTests);
        }, failShellStartup: true);

    [Fact]
    public Task ClassicDefault_KeepsEveryTrayEntryOnTheClassicWindow() =>
        RunTrayAsync(shell: null, available: true, async (tray, localizer) =>
        {
            Click(tray, localizer["Tray.Settings"]);

            Assert.Null(tray.ShellFormForTests);
            Assert.True(tray.ClassicWindowForTests.Visible);
            Assert.Equal(localizer["Tab.Settings"], tray.ClassicWindowForTests.SelectedTabTitleForTests);
            OpenMenu(tray);
            Assert.False(Item(tray, localizer["Tray.OpenClassicFallback"]).Available);
            await Task.CompletedTask;
        });

    private Task RunTrayAsync(
        string? shell,
        bool available,
        Func<TrayApplicationContext, PrintBridgeLocalizer, Task> body,
        bool failShellStartup = false)
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

                // Exit (which writes settings) before the test is reported done, while the temporary root is active.
                try
                {
                    tray?.ExitForTests();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                loop.ExitThread();
                if (failure is null)
                    completion.TrySetResult();
                else
                    completion.TrySetException(failure);
            }, null);
            System.Windows.Forms.Application.Run(loop);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        _uiThread = thread;
        thread.Start();
        return completion.Task.WaitAsync(Timeout + Timeout);
    }

    private static ToolStripMenuItem Item(TrayApplicationContext tray, string text) =>
        tray.TrayMenuForTests.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text);

    private static void Click(TrayApplicationContext tray, string text) => Item(tray, text).PerformClick();

    /// <summary>Raises the menu's Opening event, as right-clicking the tray icon does, without showing the menu.</summary>
    private static void OpenMenu(TrayApplicationContext tray)
    {
        var raise = typeof(ToolStripDropDown).GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        raise.Invoke(tray.TrayMenuForTests, [new System.ComponentModel.CancelEventArgs()]);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> find)
        where T : class
    {
        T? found = null;
        await WaitUntilAsync(() => Task.FromResult((found = find()) is not null));
        return found!;
    }

    private static async Task<string?> EvalAsync(CoreWebView2 core, string expression)
    {
        var request = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true, allowUnsafeEvalBlockedByCSP = false });
        var response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request);
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");
        return result.TryGetProperty("value", out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");
            await Task.Delay(50);
        }
    }

    private sealed class FixedProbe(bool available) : IWebView2RuntimeProbe
    {
        public WebView2RuntimeAvailability Probe() =>
            available ? new WebView2RuntimeProbe().Probe() : new WebView2RuntimeAvailability(false, null);
    }
}
