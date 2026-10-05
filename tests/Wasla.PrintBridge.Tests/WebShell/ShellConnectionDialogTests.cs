using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// The native connection dialog on a real Windows Forms message loop, over a fake engine: masking, cancel and
/// Escape, accessible errors, keeping input after a failure, the saving phase, keyboard order and right-to-left.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class ShellConnectionDialogTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly CultureScope _cultureScope = new();

    public void Dispose() => _cultureScope.Dispose();

    [Fact]
    public Task TokenField_StartsEmptyAndMasked_AndShowRevealsOnlyWhatWasTypedHere() =>
        RunDialogAsync(SentinelToken, async (dialog, _, _) =>
        {
            var (url, token, toggle, _, _, _) = dialog.PartsForTests;

            Assert.Equal(string.Empty, token.Text);
            Assert.True(token.UseSystemPasswordChar);
            Assert.Equal(SentinelServerUrl, url.Text);
            Assert.DoesNotContain(SentinelToken, AllText(dialog), StringComparison.Ordinal);

            token.Text = "typed-in-this-dialog";
            toggle.PerformClick();
            Assert.False(token.UseSystemPasswordChar);
            Assert.Equal("typed-in-this-dialog", token.Text);
            Assert.DoesNotContain(SentinelToken, AllText(dialog), StringComparison.Ordinal);

            toggle.PerformClick();
            Assert.True(token.UseSystemPasswordChar);
            await Task.CompletedTask;
        });

    [Fact]
    public Task Cancel_ClosesWithoutCheckingOrSaving() =>
        RunDialogAsync(SentinelToken, async (dialog, engine, localizer) =>
        {
            var (url, token, _, _, cancel, _) = dialog.PartsForTests;
            url.Text = "https://other.example";
            token.Text = "typed-in-this-dialog";
            Assert.Same(cancel, dialog.CancelButton);

            cancel.PerformClick();
            await WaitUntilAsync(() => !dialog.Visible);

            Assert.Equal(ShellConnectionSetupOutcome.Cancelled, dialog.Result!.Outcome);
            Assert.Equal(localizer["Shell.Setup.Cancelled"], dialog.Result.Message);
            Assert.Equal(0, engine.CheckConnectionCalls + engine.ApplyConnectionCalls);
            Assert.Equal(string.Empty, token.Text);
        });

    [Fact]
    public Task Escape_FromTheKeyboard_Cancels() =>
        RunDialogAsync(SentinelToken, async (dialog, engine, _) =>
        {
            var (_, token, _, _, _, _) = dialog.PartsForTests;
            token.Focus();
            await WaitUntilAsync(() => token.Focused);

            PostKey(token.Handle, VkEscape);
            await WaitUntilAsync(() => !dialog.Visible);

            Assert.Equal(ShellConnectionSetupOutcome.Cancelled, dialog.Result!.Outcome);
            Assert.Equal(0, engine.CheckConnectionCalls);
        });

    [Fact]
    public Task Enter_FromTheKeyboard_Connects() =>
        RunDialogAsync(string.Empty, async (dialog, engine, _) =>
        {
            var (url, token, _, connect, _, _) = dialog.PartsForTests;
            url.Text = "https://print-bridge.test";
            token.Text = "typed-in-this-dialog";
            Assert.Same(connect, dialog.AcceptButton);
            token.Focus();
            await WaitUntilAsync(() => token.Focused);

            PostKey(token.Handle, VkReturn);
            await WaitUntilAsync(() => !dialog.Visible);

            Assert.Equal(1, engine.CheckConnectionCalls);
            Assert.Equal(ShellConnectionSetupOutcome.Connected, dialog.Result!.Outcome);
        });

    [Fact]
    public Task InvalidAddress_ShowsAnAccessibleError_AndMovesFocusToTheField() =>
        RunDialogAsync(string.Empty, async (dialog, engine, localizer) =>
        {
            var (url, token, _, _, _, status) = dialog.PartsForTests;
            url.Text = "print-bridge.test";
            token.Text = "typed-in-this-dialog";

            await dialog.ConnectAsync();

            var message = localizer["Validation.InvalidServerUrl"];
            Assert.True(dialog.Visible);
            Assert.True(status.Visible);
            Assert.Equal(message, status.Text);
            Assert.Equal(AccessibleRole.Alert, status.AccessibleRole);
            Assert.StartsWith(message, url.AccessibleDescription, StringComparison.Ordinal);
            Assert.Same(url, ActiveTextBox(dialog));
            Assert.Equal(0, engine.CheckConnectionCalls);

            // Typing again clears the error state.
            url.Text = "https://print-bridge.test";
            Assert.False(status.Visible);
            Assert.Equal(localizer["Settings.ServerUrlHelp"], url.AccessibleDescription);
        });

    [Fact]
    public Task RejectedToken_KeepsTheTypedInput_OnlyWhileTheDialogIsOpen() =>
        RunDialogAsync(SentinelToken, async (dialog, engine, localizer) =>
        {
            engine.CheckConnectionFailure = PrintBridgeConnectionException.FromResponse(
                "api/print-bridge/health", SentinelServerUrl, 401, "{\"error\":\"device_auth_invalid\"}", "device_auth_invalid");
            var (_, token, _, _, cancel, status) = dialog.PartsForTests;
            token.Text = "typed-in-this-dialog";

            await dialog.ConnectAsync();

            Assert.True(dialog.Visible);
            Assert.Equal(localizer["Shell.Setup.TokenRejected"], status.Text);
            Assert.Equal("typed-in-this-dialog", token.Text);
            Assert.Same(token, ActiveTextBox(dialog));
            Assert.Equal(0, engine.ApplyConnectionCalls);

            cancel.PerformClick();
            await WaitUntilAsync(() => !dialog.Visible);
            Assert.Equal(string.Empty, token.Text);
        });

    [Fact]
    public Task Success_ClosesAsConnected_AndForgetsTheTypedToken() =>
        RunDialogAsync(string.Empty, async (dialog, engine, localizer) =>
        {
            var (url, token, _, _, _, _) = dialog.PartsForTests;
            url.Text = "https://print-bridge.test";
            token.Text = "typed-in-this-dialog";

            await dialog.ConnectAsync();

            Assert.False(dialog.Visible);
            Assert.Equal(DialogResult.OK, dialog.DialogResult);
            Assert.Equal(new ShellConnectionSetupResult(ShellConnectionSetupOutcome.Connected, localizer["Shell.Setup.Connected"]), dialog.Result);
            Assert.Equal(("https://print-bridge.test", "typed-in-this-dialog", true), engine.AppliedConnection);
            Assert.Equal(string.Empty, token.Text);
        });

    [Fact]
    public Task WhileSaving_TheDialogCannotBeClosed_AndRepeatedClicksDoNothing() =>
        RunDialogAsync(string.Empty, async (dialog, engine, localizer) =>
        {
            engine.ApplyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (url, token, _, connect, cancel, status) = dialog.PartsForTests;
            url.Text = "https://print-bridge.test";
            token.Text = "typed-in-this-dialog";

            var connecting = dialog.ConnectAsync();
            await WaitUntilAsync(() => engine.ApplyConnectionCalls == 1);

            Assert.Equal(localizer["Shell.Setup.Saving"], status.Text);
            connect.PerformClick();
            cancel.PerformClick();
            dialog.Close();
            Assert.True(dialog.Visible);
            Assert.Equal(1, engine.CheckConnectionCalls);

            engine.ApplyGate.SetResult();
            await connecting;
            Assert.False(dialog.Visible);
            Assert.Equal(ShellConnectionSetupOutcome.Connected, dialog.Result!.Outcome);
        });

    [Fact]
    public Task TabOrder_FollowsTheVisibleOrder() =>
        RunDialogAsync(SentinelToken, async (dialog, _, _) =>
        {
            var (url, token, toggle, connect, cancel, _) = dialog.PartsForTests;
            var order = new List<Control>();
            Control? current = null;
            for (var i = 0; i < 40 && order.Count < 5; i++)
            {
                current = dialog.GetNextControl(current, forward: true);
                if (current is null)
                    break;
                if (current is TextBox or Button && current.TabStop && current.CanSelect)
                    order.Add(current);
            }

            Assert.Equal([url, token, toggle, connect, cancel], order);
            await Task.CompletedTask;
        });

    [Fact]
    public Task Arabic_MirrorsTheDialog_ButKeepsAddressAndTokenLeftToRight() =>
        RunDialogAsync(SentinelToken, async (dialog, _, localizer) =>
        {
            var (url, token, _, connect, _, _) = dialog.PartsForTests;

            Assert.Equal(RightToLeft.Yes, dialog.RightToLeft);
            Assert.True(dialog.RightToLeftLayout);
            Assert.Equal(RightToLeft.No, url.RightToLeft);
            Assert.Equal(RightToLeft.No, token.RightToLeft);
            Assert.Equal(localizer["Shell.Setup.Connect"], connect.Text);
            Assert.Equal(localizer["Shell.Setup.Title.Change"], dialog.Text);
            await Task.CompletedTask;
        }, culture: SupportedCultures.Arabic);

    [Theory]
    [InlineData("", false, "Shell.Setup.Title.First", "Settings.AgentTokenHelp")]
    [InlineData(SentinelToken, false, "Shell.Setup.Title.Change", "Shell.Setup.TokenKeepHint")]
    [InlineData("", true, "Shell.Setup.Title.Reconnect", "Settings.AgentTokenHelp")]
    public Task TitleAndTokenHelp_FollowWhyTheDialogIsOpen(string savedToken, bool reconnectRequired, string titleKey, string helpKey) =>
        RunDialogAsync(savedToken, async (dialog, _, localizer) =>
        {
            Assert.Equal(localizer[titleKey], dialog.Text);
            Assert.Equal(localizer[helpKey], dialog.PartsForTests.Token.AccessibleDescription);
            await Task.CompletedTask;
        }, reconnectRequired: reconnectRequired);

    private static Task RunDialogAsync(
        string savedToken,
        Func<ShellConnectionDialog, FakeStatusSource, PrintBridgeLocalizer, Task> body,
        string culture = SupportedCultures.Turkish,
        bool reconnectRequired = false)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var loop = new ApplicationContext();
            context.Post(async _ =>
            {
                try
                {
                    var cultureService = new PrintBridgeCultureService();
                    cultureService.Initialize(culture);
                    var localizer = new PrintBridgeLocalizer(cultureService);
                    var engine = new FakeStatusSource();
                    if (reconnectRequired)
                        engine.Set(Status(BridgeServerConnectionStatus.Error, PrintBridgeConnectionReset.CreateReconnectRequiredIssue(), isRunning: false));
                    var setup = new ShellConnectionSetup(engine, ShellTestRig.NewSettings(savedToken), localizer, NullLogger.Instance, CancellationToken.None);
                    using var dialog = new ShellConnectionDialog(setup, localizer, cultureService.IsRightToLeft, dark: false)
                    {
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-32000, -32000)
                    };
                    dialog.Show();
                    await body(dialog, engine, localizer);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    loop.ExitThread();
                }
            }, null);
            System.Windows.Forms.Application.Run(loop);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(Timeout);
    }

    private static string AllText(Control root) =>
        root.Text + "|" + root.AccessibleName + "|" + root.AccessibleDescription + "|"
        + string.Join("|", root.Controls.Cast<Control>().Select(AllText));

    private static TextBox? ActiveTextBox(ContainerControl container)
    {
        Control? active = container.ActiveControl;
        while (active is ContainerControl nested && nested.ActiveControl is not null)
            active = nested.ActiveControl;
        return active as TextBox;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");
            await Task.Delay(20);
        }
    }

    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;

    /// <summary>A real key press through the Windows message loop, as the keyboard delivers it.</summary>
    private static void PostKey(IntPtr handle, int key)
    {
        const int WmKeyDown = 0x0100;
        const int WmKeyUp = 0x0101;
        PostMessage(handle, WmKeyDown, key, IntPtr.Zero);
        PostMessage(handle, WmKeyUp, key, IntPtr.Zero);
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, nint wParam, IntPtr lParam);
}
