using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.UI;

public sealed partial class MainForm
{
    private ILogger _settingsLogger = null!;
    private bool _isSavingConnection;
    private bool _isSavingPrinter;
    private bool _isSavingDeviceName;
    private bool _isSavingAdvanced;

    private void InitializeSettingsSaveUi()
    {
        _settingsLogger = _services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Wasla.PrintBridge.Settings");
    }

    private void WireConnectionSettingsChangeHandlers()
    {
        _txtServerUrl.TextChanged += (_, _) => SetSectionStatus(_lblConnectionStatus, null);
        _txtAgentToken.TextChanged += (_, _) => SetSectionStatus(_lblConnectionStatus, null);
        _cmbPrinterName.TextChanged += (_, _) => SetSectionStatus(_lblPrinterStatus, null);
        _txtDisplayName.TextChanged += (_, _) => SetSectionStatus(_lblDeviceStatus, null);
    }

    private async Task SaveConnectionSettingsAsync()
    {
        if (_isSavingConnection)
            return;

        var orderHub = new WaslaOptions
        {
            ServerUrl = _txtServerUrl.Text.Trim(),
            AgentToken = _txtAgentToken.Text.Trim()
        };

        if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(orderHub, out var errorKey))
        {
            SetSectionStatus(_lblConnectionStatus, _localizer[errorKey!], isError: true);
            return;
        }

        var wasRunning = _runtime.IsRunning;
        var previous = CaptureSnapshot();
        var bridge = ClonePrintBridgeOptions(previous.Bridge);
        var ui = CloneUiOptions(previous.Ui);
        CaptureWindowLayout(ui);
        var tokenChanged = !string.Equals(previous.OrderHub.AgentToken?.Trim(), orderHub.AgentToken, StringComparison.Ordinal);
        if (tokenChanged)
        {
            bridge.DisplayName = string.Empty;
            bridge.ServerDeviceNameResolved = false;
        }

        _isSavingConnection = true;
        SetConnectionFieldsEnabled(false);
        _btnSaveConnection.Enabled = false;
        _btnTestSettingsConnection.Enabled = false;
        SetSectionStatus(_lblConnectionStatus, _localizer["Message.ValidatingConnection"]);
        _settingsLogger.LogInformation("Connection settings save started. WasPolling={WasPolling}", wasRunning);
        try
        {
            if (wasRunning)
                await _runtime.StopAsync().ConfigureAwait(true);

            _settingsHolder.Replace(orderHub, bridge, ui);
            if (!TryPersistSettings(orderHub, bridge, ui))
            {
                _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
                if (wasRunning)
                    TryStartPolling();
                return;
            }

            var connectionVerified = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await _runtime.ValidateConnectionAsync(cts.Token).ConfigureAwait(true);
                connectionVerified = true;
                _settingsLogger.LogInformation("Token/server validation succeeded.");
                SetSectionStatus(_lblConnectionStatus, _localizer["Settings.Validation.Verified"], isSuccess: true);
            }
            catch (Exception ex)
            {
                _settingsLogger.LogWarning(ex, "Connection settings saved, but token/server validation failed.");
                _runtime.RecordConnectionFailure(ex);
                SetSectionStatus(_lblConnectionStatus, _localizer["Message.ConnectionCouldNotBeVerified"], isError: true);
            }

            if (wasRunning)
                TryStartPolling();

            SyncDeviceNameFieldFromHolder();
            RefreshDashboard();
            if (connectionVerified)
                SetSectionStatus(_lblConnectionStatus, _localizer["Message.SettingsSavedConnectionVerified"], isSuccess: true);
        }
        catch (Exception ex)
        {
            _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
            _settingsLogger.LogWarning(ex, "Connection settings save/reconnect failed.");
            SetSectionStatus(_lblConnectionStatus, _localizer.GetString("Message.SettingsSaveFailed", ex.Message), isError: true);
            if (wasRunning)
                TryStartPolling();
            RefreshDashboard();
            SyncDeviceNameFieldFromHolder();
        }
        finally
        {
            _isSavingConnection = false;
            SetConnectionFieldsEnabled(true);
            _btnSaveConnection.Enabled = true;
            _btnTestSettingsConnection.Enabled = true;
        }
    }

    private async Task TestConnectionFromSettingsAsync()
    {
        if (_isSavingConnection)
            return;

        var previous = CaptureSnapshot();
        var orderHub = new WaslaOptions
        {
            ServerUrl = _txtServerUrl.Text.Trim(),
            AgentToken = _txtAgentToken.Text.Trim()
        };

        if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(orderHub, out var errorKey))
        {
            SetSectionStatus(_lblConnectionStatus, _localizer[errorKey!], isError: true);
            return;
        }

        _isSavingConnection = true;
        SetConnectionFieldsEnabled(false);
        _btnSaveConnection.Enabled = false;
        _btnTestSettingsConnection.Enabled = false;
        SetSectionStatus(_lblConnectionStatus, _localizer["Message.ValidatingConnection"]);
        try
        {
            _settingsHolder.Replace(orderHub, previous.Bridge, previous.Ui);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _runtime.ValidateConnectionAsync(cts.Token).ConfigureAwait(true);
            SetSectionStatus(_lblConnectionStatus, _localizer["Message.ConnectionSuccess"], isSuccess: true);
            _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
            _runtime.RecordConnectionFailure(ex);
            SetSectionStatus(_lblConnectionStatus, GetUserErrorMessage(ex), isError: true);
        }
        finally
        {
            _isSavingConnection = false;
            SetConnectionFieldsEnabled(true);
            _btnSaveConnection.Enabled = true;
            _btnTestSettingsConnection.Enabled = true;
        }
    }

    private async Task ConnectWithSetupCodeAsync()
    {
        if (_isSavingConnection)
            return;

        var serverUrl = _txtServerUrl.Text.Trim();
        var setupCode = _txtSetupCode.Text.Trim();
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            SetSectionStatus(_lblConnectionStatus, _localizer["Validation.ServerUrlRequired"], isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(setupCode))
        {
            SetSectionStatus(_lblConnectionStatus, _localizer["Validation.SetupCodeRequired"], isError: true);
            return;
        }

        _isSavingConnection = true;
        SetConnectionFieldsEnabled(false);
        _btnSaveConnection.Enabled = false;
        _btnTestSettingsConnection.Enabled = false;
        _btnConnectSetupCode.Enabled = false;
        SetSectionStatus(_lblConnectionStatus, _localizer["Message.ValidatingConnection"]);

        var previous = CaptureSnapshot();
        try
        {
            var client = _services.GetRequiredService<WaslaPrintBridgeClient>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var config = await client.ExchangeSetupAsync(serverUrl, setupCode, cts.Token).ConfigureAwait(true);
            if (config is null)
            {
                SetSectionStatus(_lblConnectionStatus, _localizer["Message.SetupCodeInvalidOrExpired"], isError: true);
                return;
            }

            var hub = CloneWaslaOptions(previous.OrderHub);
            hub.ServerUrl = config.ServerUrl.Trim();
            hub.AgentToken = config.DeviceToken.Trim();
            var bridge = ClonePrintBridgeOptions(previous.Bridge);
            if (!string.IsNullOrWhiteSpace(config.DeviceName))
            {
                bridge.DisplayName = config.DeviceName.Trim();
                bridge.ServerDeviceNameResolved = true;
            }
            if (string.IsNullOrWhiteSpace(bridge.MachineName))
                bridge.MachineName = Environment.MachineName;

            if (!PrintBridgeSettingsValidator.TryValidateConnectionSettings(hub, out var errorKey))
            {
                SetSectionStatus(_lblConnectionStatus, _localizer[errorKey!], isError: true);
                return;
            }

            var ui = CloneUiOptions(previous.Ui);
            CaptureWindowLayout(ui);
            _settingsHolder.Replace(hub, bridge, ui);
            if (!TryPersistSettings(hub, bridge, ui))
            {
                _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
                return;
            }

            var connected = false;
            try
            {
                await _runtime.ValidateConnectionAsync(cts.Token).ConfigureAwait(true);
                connected = true;
            }
            catch (Exception ex)
            {
                _runtime.RecordConnectionFailure(ex);
                _settingsLogger.LogWarning(ex, "Setup code exchanged, but server connection could not be verified.");
            }

            await client.CompleteSetupAsync(config.ServerUrl, config.SessionId, config.CompletionCredential, connected, cts.Token)
                .ConfigureAwait(true);

            _txtSetupCode.Text = string.Empty;
            LoadSettingsIntoForm();
            RefreshDashboard();
            SetSectionStatus(
                _lblConnectionStatus,
                connected
                    ? _localizer["Message.SettingsSavedConnectionVerified"]
                    : _localizer["Message.ConnectionCouldNotBeVerified"],
                isSuccess: connected,
                isError: !connected);
        }
        catch (Exception ex)
        {
            _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
            _settingsLogger.LogWarning(ex, "Setup code connection failed.");
            SetSectionStatus(_lblConnectionStatus, GetUserErrorMessage(ex), isError: true);
            RefreshDashboard();
        }
        finally
        {
            _isSavingConnection = false;
            SetConnectionFieldsEnabled(true);
            _btnSaveConnection.Enabled = true;
            _btnTestSettingsConnection.Enabled = true;
            _btnConnectSetupCode.Enabled = true;
        }
    }

    private Task SavePrinterSettingsAsync()
    {
        if (_isSavingPrinter)
            return Task.CompletedTask;

        var previous = CaptureSnapshot();
        var bridge = ClonePrintBridgeOptions(previous.Bridge);
        bridge.PrinterMode = "WindowsPrinter";
        bridge.PrinterName = _cmbPrinterName.Text.Trim();

        if (!PrintBridgeSettingsValidator.TryValidatePrinterSettings(bridge, out var errorKey))
        {
            SetSectionStatus(_lblPrinterStatus, _localizer[errorKey!], isError: true);
            return Task.CompletedTask;
        }

        _isSavingPrinter = true;
        _btnSavePrinter.Enabled = false;
        try
        {
            var ui = CloneUiOptions(previous.Ui);
            CaptureWindowLayout(ui);
            if (!TryPersistSettings(previous.OrderHub, bridge, ui))
                return Task.CompletedTask;

            SetSectionStatus(_lblPrinterStatus, _localizer["Settings.SectionSaved"], isSuccess: true);
            RefreshDashboard();
        }
        finally
        {
            _isSavingPrinter = false;
            _btnSavePrinter.Enabled = true;
        }

        return Task.CompletedTask;
    }

    private async Task TestPrinterFromSettingsAsync()
    {
        var bridge = ClonePrintBridgeOptions(_settingsHolder.Snapshot().Bridge);
        bridge.PrinterMode = "WindowsPrinter";
        bridge.PrinterName = _cmbPrinterName.Text.Trim();

        if (!PrintBridgeSettingsValidator.TryValidatePrinterAvailability(bridge, out var errorKey))
        {
            SetSectionStatus(_lblPrinterStatus, _localizer[errorKey!], isError: true);
            return;
        }

        try
        {
            _btnTestSettingsPrinter.Enabled = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var printer = _services.GetRequiredService<IReceiptPrinter>();
            var receipt = $"Wasla Print Bridge{Environment.NewLine}Test print{Environment.NewLine}{DateTime.Now:G}";
            await printer.PrintAsync(bridge.PrinterName, receipt, 1, cts.Token).ConfigureAwait(true);
            SetSectionStatus(_lblPrinterStatus, _localizer["Message.TestPrintSent"], isSuccess: true);
        }
        catch (Exception ex)
        {
            SetSectionStatus(_lblPrinterStatus, GetUserErrorMessage(ex), isError: true);
        }
        finally
        {
            _btnTestSettingsPrinter.Enabled = true;
        }
    }

    private Task SaveDeviceIdentitySettingsAsync()
    {
        if (_isSavingDeviceName)
            return Task.CompletedTask;

        var previous = CaptureSnapshot();
        var bridge = ClonePrintBridgeOptions(previous.Bridge);
        bridge.BridgeName = _txtDisplayName.Text.Trim();
        bridge.MachineName = Environment.MachineName;

        if (!PrintBridgeSettingsValidator.TryValidateDeviceIdentitySettings(bridge, out var errorKey))
        {
            SetSectionStatus(_lblDeviceStatus, _localizer[errorKey!], isError: true);
            return Task.CompletedTask;
        }

        _isSavingDeviceName = true;
        _btnSaveDeviceName.Enabled = false;
        try
        {
            var ui = CloneUiOptions(previous.Ui);
            CaptureWindowLayout(ui);
            if (TryPersistSettings(previous.OrderHub, bridge, ui))
            {
                _txtDisplayName.Text = bridge.BridgeName;
                SetSectionStatus(_lblDeviceStatus, _localizer["Settings.SectionSaved"], isSuccess: true);
                RefreshDashboard();
            }
        }
        finally
        {
            _isSavingDeviceName = false;
            _btnSaveDeviceName.Enabled = true;
        }

        return Task.CompletedTask;
    }

    private Task SaveAdvancedSettingsAsync()
    {
        if (_isSavingAdvanced)
            return Task.CompletedTask;

        var previous = CaptureSnapshot();
        var bridge = ClonePrintBridgeOptions(previous.Bridge);
        bridge.DryRun = _chkDryRun.Checked;
        bridge.IdlePollIntervalSeconds = (int)_numIdlePoll.Value;
        bridge.BusyPollIntervalSeconds = (int)_numBusyPoll.Value;
        bridge.ErrorPollIntervalSeconds = (int)_numErrorPoll.Value;

        if (!PrintBridgeSettingsValidator.TryValidateAdvancedBehaviorSettings(bridge, out var errorKey))
        {
            SetSectionStatus(_lblAdvancedStatus, _localizer[errorKey!], isError: true);
            return Task.CompletedTask;
        }

        _isSavingAdvanced = true;
        _btnSaveAdvanced.Enabled = false;
        try
        {
            var ui = CloneUiOptions(previous.Ui);
            CaptureWindowLayout(ui);
            if (TryPersistSettings(previous.OrderHub, bridge, ui))
            {
                SetSectionStatus(_lblAdvancedStatus, _localizer["Settings.SectionSaved"], isSuccess: true);
                RefreshDashboard();
            }
        }
        finally
        {
            _isSavingAdvanced = false;
            _btnSaveAdvanced.Enabled = true;
        }

        return Task.CompletedTask;
    }

    private bool TryPersistSettings(WaslaOptions orderHub, PrintBridgeOptions bridge, UiOptions ui)
    {
        PrintBridgeWindowLayout.CaptureInto(ui, this);

        try
        {
            _settingsStore.Save(new PrintBridgeSettingsStore.AppSettingsDocument
            {
                OrderHub = orderHub,
                PrintBridge = bridge,
                Ui = ui
            });
            _settingsHolder.Replace(orderHub, bridge, ui);
            _settingsLogger.LogInformation("Settings saved.");
            return true;
        }
        catch (Exception ex)
        {
            _settingsLogger.LogWarning(ex, "Settings save failed.");
            MessageBox.Show(
                _localizer.GetString("Message.SettingsSaveFailed", ex.Message),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private void TryStartPolling()
    {
        try
        {
            if (!_runtime.IsRunning)
                _runtime.Start();
        }
        catch (Exception ex)
        {
            _runtime.RecordConnectionFailure(ex);
            _settingsLogger.LogWarning(ex, "Polling could not be restarted after settings save.");
        }
    }

    private (WaslaOptions OrderHub, PrintBridgeOptions Bridge, UiOptions Ui) CaptureSnapshot()
    {
        var snapshot = _settingsHolder.Snapshot();
        return (
            CloneWaslaOptions(snapshot.OrderHub),
            ClonePrintBridgeOptions(snapshot.Bridge),
            CloneUiOptions(snapshot.Ui));
    }

    private void CaptureWindowLayout(UiOptions ui) =>
        PrintBridgeWindowLayout.CaptureInto(ui, this);

    private static WaslaOptions CloneWaslaOptions(WaslaOptions source) =>
        new()
        {
            ServerUrl = source.ServerUrl,
            AgentToken = source.AgentToken
        };

    private static PrintBridgeOptions ClonePrintBridgeOptions(PrintBridgeOptions source) =>
        new()
        {
            PrinterMode = source.PrinterMode,
            PrinterName = source.PrinterName,
            IdlePollIntervalSeconds = source.IdlePollIntervalSeconds,
            BusyPollIntervalSeconds = source.BusyPollIntervalSeconds,
            ErrorPollIntervalSeconds = source.ErrorPollIntervalSeconds,
            MaxJobsPerPoll = source.MaxJobsPerPoll,
            DryRun = source.DryRun,
            DisplayName = source.DisplayName,
            ServerDeviceNameResolved = source.ServerDeviceNameResolved,
            MachineName = source.MachineName,
            BridgeName = source.BridgeName
        };

    private static UiOptions CloneUiOptions(UiOptions source) =>
        new()
        {
            Language = source.Language,
            StartWithWindows = source.StartWithWindows,
            MinimizeToTray = source.MinimizeToTray,
            WindowWidth = source.WindowWidth,
            WindowHeight = source.WindowHeight,
            WindowLeft = source.WindowLeft,
            WindowTop = source.WindowTop,
            WindowState = source.WindowState
        };

    private void SetConnectionFieldsEnabled(bool enabled)
    {
        _txtServerUrl.Enabled = enabled;
        _txtAgentToken.Enabled = enabled;
        _txtSetupCode.Enabled = enabled;
        _btnToggleToken.Enabled = enabled;
    }

    private static void SetSectionStatus(Label label, string? message, bool isError = false, bool isSuccess = false)
    {
        if (label is null)
            return;

        label.Visible = !string.IsNullOrWhiteSpace(message);
        label.Text = message ?? string.Empty;
        label.ForeColor = isError
            ? PrintBridgeUiTheme.Danger
            : isSuccess
                ? PrintBridgeUiTheme.Success
                : PrintBridgeUiTheme.TextMuted;
    }

}
