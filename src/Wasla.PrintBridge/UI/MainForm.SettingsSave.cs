using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.UI;

public sealed partial class MainForm
{
    private ILogger _settingsLogger = null!;
    private bool _isSavingConnection;
    private bool _isSavingPrinter;
    private bool _isSavingAdvanced;

    private void InitializeSettingsSaveUi()
    {
        _settingsLogger = _services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Wasla.PrintBridge.Settings");
    }

    private void WireConnectionSettingsChangeHandlers()
    {
        _txtServerUrl.TextChanged += (_, _) =>
        {
            if (PrintBridgeConnectionFieldSync.ShouldClearConnectionStatusOnFieldTextChange(
                    _syncingConnectionFields,
                    _txtServerUrl.Focused))
                SetSectionStatus(_lblConnectionStatus, null);
        };
        _txtAgentToken.TextChanged += (_, _) =>
        {
            if (!PrintBridgeConnectionFieldSync.ShouldTreatAgentTokenTextChangeAsUserEdit(
                    _syncingConnectionFields,
                    _txtAgentToken.Focused))
            {
                RefreshAgentTokenPasteGuard();
                return;
            }

            _agentTokenUserEdited = true;
            SetSectionStatus(_lblConnectionStatus, null);
            RefreshAgentTokenPasteGuard();
        };
        _cmbPrinterName.TextChanged += (_, _) => SetSectionStatus(_lblPrinterStatus, null);
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

        // Refused while a print job is being completed; from here until polling restarts no job is claimed.
        var change = TryBeginConnectionChange();
        if (change is null)
            return;

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
                change.Dispose();
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
                ResetAgentTokenInputTracking();
                SyncConnectionFieldsFromHolder();
                SetSectionStatus(_lblConnectionStatus, GetUserErrorMessage(ex), isError: true);
            }

            change.Dispose();
            if (wasRunning)
                TryStartPolling();

            ResetAgentTokenInputTracking();
            RefreshDashboard();
            if (connectionVerified && PrintBridgeRuntimeStatus.ShouldReportConnectionSuccess(_runtime.GetStatus()))
                SetSectionStatus(_lblConnectionStatus, _localizer["Message.SettingsSavedConnectionVerified"], isSuccess: true);
            else
                RefreshSettingsConnectionStatus();
        }
        catch (Exception ex)
        {
            _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
            _settingsLogger.LogWarning(ex, "Connection settings save/reconnect failed.");
            SetSectionStatus(_lblConnectionStatus, _localizer.GetString("Message.SettingsSaveFailed", ex.Message), isError: true);
            change.Dispose();
            if (wasRunning)
                TryStartPolling();
            RefreshDashboard();
        }
        finally
        {
            change.Dispose();
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

        // The test puts the typed connection in place for a moment, so no job may be active or start meanwhile.
        var change = TryBeginConnectionChange();
        if (change is null)
            return;

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

            var tokenChanged = !string.Equals(
                previous.OrderHub.AgentToken?.Trim(),
                orderHub.AgentToken,
                StringComparison.Ordinal);
            if (tokenChanged)
                _settingsHolder.Replace(orderHub, previous.Bridge, previous.Ui);
            else
                _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);

            ResetAgentTokenInputTracking();
            RefreshDashboard();
            if (PrintBridgeRuntimeStatus.ShouldReportConnectionSuccess(_runtime.GetStatus()))
            {
                change.Dispose();
                TryStartPolling();
                SetSectionStatus(_lblConnectionStatus, _localizer["Message.ConnectionSuccess"], isSuccess: true);
            }
            else
                RefreshSettingsConnectionStatus();
        }
        catch (Exception ex)
        {
            var shouldClearToken = ex is PrintBridgeConnectionException connectionEx
                && new PrintBridgeRuntimeIssue(
                    connectionEx.IssueCode,
                    connectionEx.UserMessageKey,
                    connectionEx.FormatArgs).ShouldClearToken;
            if (shouldClearToken)
            {
                _runtime.RecordConnectionFailure(ex);
                var snapshot = _settingsHolder.Snapshot();
                var testedMatchesPersisted = string.Equals(
                    previous.OrderHub.AgentToken?.Trim(),
                    orderHub.AgentToken,
                    StringComparison.Ordinal);
                if (testedMatchesPersisted)
                    TryPersistSettings(snapshot.OrderHub, snapshot.Bridge, snapshot.Ui);
                else
                    _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);

                ResetAgentTokenInputTracking();
                SyncConnectionFieldsFromHolder();
            }
            else
            {
                _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
                SyncConnectionFieldsFromHolder();
            }

            SetSectionStatus(_lblConnectionStatus, GetUserErrorMessage(ex), isError: true);
            RefreshDashboard();
            RefreshSettingsConnectionStatus();
        }
        finally
        {
            change.Dispose();
            _isSavingConnection = false;
            SetConnectionFieldsEnabled(true);
            _btnSaveConnection.Enabled = true;
            _btnTestSettingsConnection.Enabled = true;
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

    /// <summary>
    /// Asks the engine whether the connection may change now (<see cref="IPrintBridgeConnectionGuard"/>). While a print
    /// job is being completed it may not: nothing is changed and the section says so.
    /// </summary>
    private IDisposable? TryBeginConnectionChange()
    {
        var change = _runtime.TryBeginConnectionChange();
        if (change is null)
        {
            _settingsLogger.LogInformation("Connection settings were not changed because a print job is still being completed.");
            SetSectionStatus(_lblConnectionStatus, _localizer["Settings.Connection.PrintingInProgress"], isError: true);
        }

        return change;
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

    private async Task ResetConnectionAsync()
    {
        if (_isSavingConnection)
            return;

        if (!PrintBridgeConfirmDialog.Confirm(
                this,
                _localizer["Message.ResetConnectionConfirmTitle"],
                _localizer["Message.ResetConnectionConfirm"],
                _localizer["Button.ResetConnectionConfirm"],
                _localizer["Button.ResetConnectionCancel"]))
            return;

        _isSavingConnection = true;
        SetConnectionFieldsEnabled(false);
        _btnSaveConnection.Enabled = false;
        _btnTestSettingsConnection.Enabled = false;
        _btnResetConnection.Enabled = false;
        SetSectionStatus(_lblConnectionStatus, null);
        _settingsLogger.LogInformation("Connection reset requested by user.");

        try
        {
            await _runtime.ResetConnectionForReconnectAsync().ConfigureAwait(true);
            ResetAgentTokenInputTracking();
            SyncConnectionFieldsFromHolder();
            RefreshDashboard();
            RefreshSettingsConnectionStatus();
            SelectSettingsTab();
        }
        catch (Exception ex)
        {
            _settingsLogger.LogWarning(ex, "Connection reset failed.");
            SetSectionStatus(_lblConnectionStatus, GetUserErrorMessage(ex), isError: true);
        }
        finally
        {
            _isSavingConnection = false;
            SetConnectionFieldsEnabled(true);
            _btnSaveConnection.Enabled = true;
            _btnTestSettingsConnection.Enabled = true;
            _btnResetConnection.Enabled = true;
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

    internal static PrintBridgeOptions ClonePrintBridgeOptions(PrintBridgeOptions source) =>
        new()
        {
            PrinterMode = source.PrinterMode,
            PrinterName = source.PrinterName,
            IdlePollIntervalSeconds = source.IdlePollIntervalSeconds,
            BusyPollIntervalSeconds = source.BusyPollIntervalSeconds,
            ErrorPollIntervalSeconds = source.ErrorPollIntervalSeconds,
            MaxJobsPerPoll = source.MaxJobsPerPoll,
            DryRun = source.DryRun,
            InstallationId = source.InstallationId,
            DisplayName = source.DisplayName,
            ServerDeviceNameResolved = source.ServerDeviceNameResolved,
            MachineName = source.MachineName,
            BridgeName = source.BridgeName
        };

    internal static UiOptions CloneUiOptions(UiOptions source) =>
        new()
        {
            Language = source.Language,
            StartWithWindows = source.StartWithWindows,
            MinimizeToTray = source.MinimizeToTray,
            WindowWidth = source.WindowWidth,
            WindowHeight = source.WindowHeight,
            WindowLeft = source.WindowLeft,
            WindowTop = source.WindowTop,
            WindowState = source.WindowState,
            Shell = source.Shell
        };

    private void SetConnectionFieldsEnabled(bool enabled)
    {
        _txtServerUrl.Enabled = enabled;
        _txtAgentToken.Enabled = enabled;
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
