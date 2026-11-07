using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// WAS-91: the native confirmation before a setup link moves Print Bridge to another restaurant, shown modally on a real
/// Windows Forms message loop (off-screen) with fictional names. It defaults to Cancel: Escape, Enter on the initial
/// focus and closing the window all cancel, and only the confirm button approves. It shows the verified restaurants and
/// servers in every culture, mirrored in Arabic with the addresses left to right, in both palettes.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed partial class ShellTransferConfirmationTests : IDisposable
{
    private const string CurrentName = "Anadolu Sofrası";
    private const string TargetName = "Bahçe & Burger";
    private const string CurrentHost = "anadolu.app.wasla.tr";
    private const string TargetHost = "bahce.app.wasla.tr";
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int WmClose = 0x0010;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly PrintBridgeConnectionTransferConsentRequest Request = new(CurrentName, CurrentHost, TargetName, TargetHost);

    private static readonly string[] TransferKeys =
    [
        "Auto.Transfer.Title", "Auto.Transfer.Message", "Auto.Transfer.Confirm", "Auto.Transfer.CurrentRestaurant",
        "Auto.Transfer.CurrentServer", "Auto.Transfer.NewRestaurant", "Auto.Transfer.NewServer", "Auto.Transfer.UnknownRestaurant",
        "Auto.Cancelled", "Auto.CurrentConnectionUnverified", "Auto.DeviceLimitReached", "Auto.InstallationConflict",
        "Auto.InstallationRegisteredToAnotherRestaurant", "Auto.ChangePending", "Auto.DestinationDeviceDisabled", "Auto.PreviousChangeCompleted"
    ];

    private readonly CultureScope _cultureScope = new();

    public void Dispose() => _cultureScope.Dispose();

    [Theory]
    [InlineData(SupportedCultures.Turkish)]
    [InlineData(SupportedCultures.English)]
    [InlineData(SupportedCultures.Arabic)]
    [InlineData(SupportedCultures.Russian)]
    public Task ItStartsOnCancel_AndShowsTheVerifiedRestaurantsAndServers(string culture) =>
        RunAsync(culture, dark: false, async (dialog, localizer) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);

            Assert.Same(dialog.Cancel, dialog.CancelButton);
            Assert.Null(dialog.AcceptButton);
            Assert.Equal(localizer["Auto.Transfer.Title"], dialog.Text);
            Assert.Equal(localizer["Auto.Transfer.Confirm"], dialog.Confirm.Text);
            Assert.Equal(localizer["Shell.Action.Cancel"], dialog.Cancel.Text);
            Assert.Equal(localizer.GetString("Auto.Transfer.Message", CurrentName, TargetName), dialog.Message.Text);
            Assert.Equal(dialog.Message.Text, dialog.AccessibleDescription);
            Assert.Equal(
                [CurrentName, CurrentHost, TargetName, TargetHost],
                dialog.DetailValues.Select(v => v.Text));
            Assert.Equal(
                [localizer["Auto.Transfer.CurrentRestaurant"], localizer["Auto.Transfer.CurrentServer"], localizer["Auto.Transfer.NewRestaurant"], localizer["Auto.Transfer.NewServer"]],
                dialog.DetailValues.Select(v => v.AccessibleName));

            // An ampersand in a restaurant name is shown, not turned into a keyboard mnemonic.
            Assert.False(dialog.Message.UseMnemonic);
            Assert.All(dialog.DetailValues, v => Assert.False(v.UseMnemonic));
            dialog.Close();
        });

    [Fact]
    public async Task Escape_Cancels()
    {
        var result = await RunAsync(SupportedCultures.Turkish, dark: false, async (dialog, _) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);
            PostKey(dialog.Cancel.Handle, VkEscape);
        });

        Assert.Equal(DialogResult.Cancel, result);
    }

    [Fact]
    public async Task Enter_OnTheInitialFocus_Cancels()
    {
        var result = await RunAsync(SupportedCultures.English, dark: false, async (dialog, _) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);
            PostKey(dialog.Cancel.Handle, VkReturn);
        });

        Assert.Equal(DialogResult.Cancel, result);
    }

    [Fact]
    public async Task ClosingTheWindow_Cancels()
    {
        var result = await RunAsync(SupportedCultures.Russian, dark: true, async (dialog, _) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);
            PostMessage(dialog.Handle, WmClose, 0, IntPtr.Zero);
        });

        Assert.Equal(DialogResult.Cancel, result);
    }

    [Fact]
    public async Task OnlyTheConfirmButton_Approves_AndTabReachesIt()
    {
        var result = await RunAsync(SupportedCultures.Arabic, dark: false, async (dialog, _) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);
            Assert.True(dialog.Confirm.TabStop);
            Assert.True(dialog.Confirm.TabIndex < dialog.Cancel.TabIndex);
            dialog.SelectNextControl(dialog.Cancel, forward: true, tabStopOnly: true, nested: true, wrap: true);
            await WaitUntilAsync(() => dialog.Confirm.Focused);
            PostKey(dialog.Confirm.Handle, VkReturn);
        });

        Assert.Equal(DialogResult.OK, result);
    }

    [Fact]
    public Task Arabic_MirrorsTheDialog_ButKeepsTheServersLeftToRight() =>
        RunAsync(SupportedCultures.Arabic, dark: true, async (dialog, _) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);
            Assert.Equal(RightToLeft.Yes, dialog.RightToLeft);
            Assert.True(dialog.RightToLeftLayout);
            Assert.Equal(RightToLeft.No, dialog.DetailValues[1].RightToLeft);
            Assert.Equal(RightToLeft.No, dialog.DetailValues[3].RightToLeft);
            Assert.Equal(RightToLeft.Yes, dialog.DetailValues[0].RightToLeft); // a restaurant name follows the language
            Assert.Matches(ArabicLetters(), dialog.Message.Text);
            dialog.Close();
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ItUsesTheAppsPalette(bool dark) =>
        RunAsync(SupportedCultures.Turkish, dark, async (dialog, _) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);
            var palette = ShellPalette.For(dark);
            Assert.Equal(palette.Surface, dialog.BackColor);
            Assert.Equal(palette.Text, dialog.Message.ForeColor);
            Assert.All(dialog.DetailValues, v => Assert.Equal(palette.Text, v.ForeColor));
            dialog.Close();
        });

    [Theory]
    [InlineData(SupportedCultures.Russian)]
    [InlineData(SupportedCultures.Arabic)]
    public Task LongNames_Wrap_AndTheDialogStaysInsideTheSmallestSupportedScreen(string culture) =>
        RunAsync(culture, dark: false, async (dialog, _) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);
            var scale = dialog.DeviceDpi / 96F;
            // The app window's smallest size is 420 × 420; the confirmation must fit a 1024 × 600 (netbook) screen.
            Assert.True(dialog.Width <= 600 * scale, $"Width {dialog.Width} at {dialog.DeviceDpi} DPI.");
            Assert.True(dialog.Height <= 600 * scale, $"Height {dialog.Height} at {dialog.DeviceDpi} DPI.");
            Assert.All(dialog.DetailValues, v => Assert.True(v.Right <= v.Parent!.ClientSize.Width, "A value runs past its panel."));
            Assert.True(dialog.Confirm.Bottom <= dialog.ClientSize.Height && dialog.Cancel.Bottom <= dialog.ClientSize.Height);
            dialog.Close();
        }, request: new(new string('Ş', 60) + " Restoran", CurrentHost, "Очень длинное название ресторана для проверки переноса", TargetHost));

    [Fact]
    public Task AMissingName_IsShownAsUnnamed_NeverAsBlank() =>
        RunAsync(SupportedCultures.English, dark: false, async (dialog, localizer) =>
        {
            await WaitUntilAsync(() => dialog.Cancel.Focused);
            Assert.Equal(localizer["Auto.Transfer.UnknownRestaurant"], dialog.DetailValues[2].Text);
            dialog.Close();
        }, request: Request with { TargetTenantName = " " });

    [Theory]
    [InlineData(PrintBridgeAutoSetupOutcome.Cancelled, "Auto.Cancelled", MessageBoxIcon.Information)]
    [InlineData(PrintBridgeAutoSetupOutcome.CurrentConnectionUnverified, "Auto.CurrentConnectionUnverified", MessageBoxIcon.Warning)]
    [InlineData(PrintBridgeAutoSetupOutcome.DeviceLimitReached, "Auto.DeviceLimitReached", MessageBoxIcon.Warning)]
    [InlineData(PrintBridgeAutoSetupOutcome.InstallationConflict, "Auto.InstallationConflict", MessageBoxIcon.Warning)]
    [InlineData(PrintBridgeAutoSetupOutcome.ChangePending, "Auto.ChangePending", MessageBoxIcon.Warning)]
    [InlineData(PrintBridgeAutoSetupOutcome.DestinationDeviceDisabled, "Auto.DestinationDeviceDisabled", MessageBoxIcon.Warning)]
    public void EveryMoveOutcomeThatSavedNothing_IsShownAsNotApplied(PrintBridgeAutoSetupOutcome outcome, string key, MessageBoxIcon icon)
    {
        var view = TrayApplicationContext.DescribeAutoSetupOutcome(outcome);

        Assert.Equal((key, icon, false, false), (view.MessageKey, view.Icon, view.Saved, view.Connected));
    }

    [Fact]
    public void AnEarlierMoveFinishedFirst_IsShownAsConnected_AndTheNewLinkAsNotApplied()
    {
        var view = TrayApplicationContext.DescribeAutoSetupOutcome(PrintBridgeAutoSetupOutcome.PreviousChangeCompleted);

        // Saved: the tray verifies the connection and resumes listening; the message says the new link was not applied.
        Assert.Equal(("Auto.PreviousChangeCompleted", MessageBoxIcon.Information, true, true), (view.MessageKey, view.Icon, view.Saved, view.Connected));
    }

    [Fact]
    public void TheMoveTexts_AreTranslatedInEveryCulture_WithTheirPlaceholders_AndNoAddressOrSecret()
    {
        var turkish = Load("PrintBridgeResources.tr-TR.resx");
        foreach (var (file, culture) in new[]
                 {
                     ("PrintBridgeResources.resx", "neutral"), ("PrintBridgeResources.tr-TR.resx", "tr"),
                     ("PrintBridgeResources.en-US.resx", "en"), ("PrintBridgeResources.ar-SA.resx", "ar"),
                     ("PrintBridgeResources.ru-RU.resx", "ru")
                 })
        {
            var values = Load(file);
            foreach (var key in TransferKeys)
            {
                Assert.True(values.TryGetValue(key, out var value), $"{file} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: {key} is empty.");
                Assert.DoesNotMatch(AddressOrSecret(), value);

                var placeholders = Placeholders().Matches(value).Select(m => m.Value).Distinct().Order().ToArray();
                Assert.Equal(key == "Auto.Transfer.Message" ? ["{0}", "{1}"] : [], placeholders);

                if (culture is "en" or "ar" or "ru")
                    Assert.NotEqual(turkish[key], value);
                if (culture == "ar")
                    Assert.Matches(ArabicLetters(), value);
                if (culture == "ru")
                    Assert.Matches(CyrillicLetters(), value);
                if (culture == "neutral")
                    Assert.Equal(turkish[key], value);
            }
        }
    }

    // Helpers ------------------------------------------------------------------------------------

    /// <summary>Shows the confirmation modally, off-screen, runs <paramref name="whileShown"/> and returns its result.</summary>
    private static Task<DialogResult> RunAsync(
        string culture,
        bool dark,
        Func<ShellConfirmDialog, PrintBridgeLocalizer, Task> whileShown,
        PrintBridgeConnectionTransferConsentRequest? request = null)
    {
        var completion = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var loop = new ApplicationContext();
            context.Post(_ =>
            {
                Exception? failure = null;
                try
                {
                    var cultureService = new PrintBridgeCultureService();
                    cultureService.Initialize(culture);
                    var localizer = new PrintBridgeLocalizer(cultureService);
                    using var dialog = ShellConfirmDialog.ForConnectionTransfer(request ?? Request, localizer, cultureService.IsRightToLeft, dark);
                    dialog.StartPosition = FormStartPosition.Manual;
                    dialog.Location = new Point(-32000, -32000);
                    dialog.Shown += async (_, _) =>
                    {
                        try
                        {
                            await whileShown(dialog, localizer);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            dialog.Close();
                        }
                    };

                    var result = dialog.ShowDialog();
                    if (failure is not null)
                        completion.TrySetException(failure);
                    else
                        completion.TrySetResult(result);
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

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Wasla.PrintBridge", "Resources", file)).Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty, StringComparer.Ordinal);

    [GeneratedRegex(@"https?://|token|wasla-printbridge:|app\.wasla", RegexOptions.IgnoreCase)]
    private static partial Regex AddressOrSecret();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholders();

    [GeneratedRegex(@"\p{IsArabic}")]
    private static partial Regex ArabicLetters();

    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex CyrillicLetters();
}
