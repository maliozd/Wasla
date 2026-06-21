using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.UI;

public sealed partial class MainForm
{
    private ProgressBar _saveValidationProgress = null!;
    private Label _lblSaveValidationStatus = null!;
    private ILogger _settingsLogger = null!;
    private bool _isSavingSettings;

    private void InitializeSettingsSaveUi(FlowLayoutPanel savePanel)
    {
        _settingsLogger = _services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Wasla.PrintBridge.Settings");

        _saveValidationProgress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Width = 120,
            Height = 22,
            Visible = false,
            Margin = new Padding(12, 6, 0, 0)
        };
        _lblSaveValidationStatus = new Label
        {
            AutoSize = true,
            ForeColor = PrintBridgeUiTheme.TextMuted,
            Margin = new Padding(12, 8, 0, 0),
            Visible = false
        };

        savePanel.Controls.Add(_saveValidationProgress);
        savePanel.Controls.Add(_lblSaveValidationStatus);
    }

    private void WireConnectionSettingsChangeHandlers()
    {
        _txtServerUrl.TextChanged += (_, _) => ResetSaveValidationStatus();
        _txtAgentToken.TextChanged += (_, _) => ResetSaveValidationStatus();
    }

    private void ResetSaveValidationStatus()
    {
        if (_isSavingSettings)
            return;

        SetSaveValidationUi(SaveValidationUiState.Idle);
    }

    private async Task SaveSettingsAsync()
    {
        if (_isSavingSettings)
            return;

        var snapshot = _settingsHolder.Snapshot();
        var previous = (
            OrderHub: CloneWaslaOptions(snapshot.OrderHub),
            Bridge: ClonePrintBridgeOptions(snapshot.Bridge),
            Ui: CloneUiOptions(snapshot.Ui));
        var orderHub = new WaslaOptions
        {
            ServerUrl = _txtServerUrl.Text.Trim(),
            AgentToken = _txtAgentToken.Text.Trim()
        };

        var bridge = BuildBridgeOptionsFromForm(previous.Bridge);
        var tokenChanged = !string.Equals(
            previous.OrderHub.AgentToken?.Trim(),
            orderHub.AgentToken,
            StringComparison.Ordinal);
        if (tokenChanged)
        {
            bridge.DisplayName = string.Empty;
            bridge.ServerDeviceNameResolved = false;
        }

        if (!PrintBridgeSettingsValidator.TryValidate(orderHub, bridge, out var errorKey))
        {
            MessageBox.Show(_localizer[errorKey!], PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var previousLanguage = previous.Ui.Language;
        var ui = CloneUiOptions(previous.Ui);
        PrintBridgeWindowLayout.CaptureInto(ui, this);
        if (_cmbLanguage.SelectedItem is LanguageOption languageOption)
            ui.Language = languageOption.CultureName;

        _settingsLogger.LogInformation("Settings save started.");

        await SaveSettingsWithConnectionValidationAsync(previous, orderHub, bridge, ui, previousLanguage)
            .ConfigureAwait(true);
    }

    private async Task SaveSettingsWithConnectionValidationAsync(
        (WaslaOptions OrderHub, PrintBridgeOptions Bridge, UiOptions Ui) previous,
        WaslaOptions orderHub,
        PrintBridgeOptions bridge,
        UiOptions ui,
        string? previousLanguage)
    {
        _isSavingSettings = true;
        SetSaveValidationUi(SaveValidationUiState.Validating);
        SetConnectionFieldsEnabled(false);
        _btnSaveSettings.Enabled = false;

        var wasRunning = _runtime.IsRunning;
        _settingsLogger.LogInformation("Settings save and reconnect started. WasPolling={WasPolling}", wasRunning);

        try
        {
            if (wasRunning)
                await _runtime.StopAsync().ConfigureAwait(true);

            _settingsHolder.Replace(orderHub, bridge, ui);
            if (!TryPersistSettings(orderHub, bridge, ui, previousLanguage, out var languageChanged))
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
                SetSaveValidationUi(SaveValidationUiState.Verified);
            }
            catch (Exception ex)
            {
                _settingsLogger.LogWarning(ex, "Settings saved, but token/server validation failed.");
                _runtime.RecordConnectionFailure(ex);
                SetSaveValidationUi(SaveValidationUiState.Failed);
            }

            if (wasRunning)
                TryStartPolling();

            ShowSettingsSavedMessage(connectionVerified, languageChanged);
            SyncDeviceNameFieldFromHolder();
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
            _settingsLogger.LogWarning(ex, "Settings save/reconnect failed.");
            SetSaveValidationUi(SaveValidationUiState.Failed);
            MessageBox.Show(
                _localizer.GetString("Message.SettingsSaveFailed", ex.Message),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            if (wasRunning)
                TryStartPolling();
            RefreshDashboard();
            SyncDeviceNameFieldFromHolder();
        }
        finally
        {
            _isSavingSettings = false;
            SetConnectionFieldsEnabled(true);
            _btnSaveSettings.Enabled = true;
        }
    }

    private bool TryPersistSettings(
        WaslaOptions orderHub,
        PrintBridgeOptions bridge,
        UiOptions ui,
        string? previousLanguage,
        out bool languageChanged)
    {
        var previousNormalized = string.IsNullOrWhiteSpace(previousLanguage)
            ? _cultureService.CurrentCulture.Name
            : SupportedCultures.NormalizeOrDefault(previousLanguage);
        var newNormalized = SupportedCultures.NormalizeOrDefault(ui.Language);
        languageChanged = !string.Equals(previousNormalized, newNormalized, StringComparison.OrdinalIgnoreCase);

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

    private void ShowSettingsSavedMessage(bool connectionVerified, bool languageChanged)
    {
        var message = connectionVerified
            ? _localizer["Message.SettingsSavedConnectionVerified"]
            : $"{_localizer["Message.SettingsSaved"]}{Environment.NewLine}{_localizer["Message.ConnectionCouldNotBeVerified"]}";

        if (languageChanged)
            message = $"{message}{Environment.NewLine}{_localizer["Message.LanguageRestartRequired"]}";

        MessageBox.Show(message, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    private PrintBridgeOptions BuildBridgeOptionsFromForm(PrintBridgeOptions metadataSource)
    {
        return new PrintBridgeOptions
        {
            PrinterMode = "WindowsPrinter",
            PrinterName = _cmbPrinterName.Text.Trim(),
            DisplayName = metadataSource.DisplayName,
            ServerDeviceNameResolved = metadataSource.ServerDeviceNameResolved,
            MachineName = Environment.MachineName,
            DryRun = _chkDryRun.Checked,
            IdlePollIntervalSeconds = (int)_numIdlePoll.Value,
            BusyPollIntervalSeconds = (int)_numBusyPoll.Value,
            ErrorPollIntervalSeconds = (int)_numErrorPoll.Value,
            MaxJobsPerPoll = metadataSource.MaxJobsPerPoll
        };
    }

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
        _btnToggleToken.Enabled = enabled;
    }

    private enum SaveValidationUiState
    {
        Idle,
        Validating,
        Verified,
        Failed
    }

    private void SetSaveValidationUi(SaveValidationUiState state)
    {
        switch (state)
        {
            case SaveValidationUiState.Validating:
                _saveValidationProgress.Visible = true;
                _saveValidationProgress.Style = ProgressBarStyle.Marquee;
                _lblSaveValidationStatus.Visible = true;
                _lblSaveValidationStatus.ForeColor = PrintBridgeUiTheme.TextMuted;
                _lblSaveValidationStatus.Text = _localizer["Message.ValidatingConnection"];
                break;
            case SaveValidationUiState.Verified:
                _saveValidationProgress.Visible = false;
                _lblSaveValidationStatus.Visible = true;
                _lblSaveValidationStatus.ForeColor = PrintBridgeUiTheme.Success;
                _lblSaveValidationStatus.Text = _localizer["Settings.Validation.Verified"];
                break;
            case SaveValidationUiState.Failed:
                _saveValidationProgress.Visible = false;
                _lblSaveValidationStatus.Visible = true;
                _lblSaveValidationStatus.ForeColor = PrintBridgeUiTheme.Danger;
                _lblSaveValidationStatus.Text = _localizer["Settings.Validation.Failed"];
                break;
            default:
                _saveValidationProgress.Visible = false;
                _lblSaveValidationStatus.Visible = false;
                _lblSaveValidationStatus.Text = string.Empty;
                break;
        }
    }

}
