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
        _txtBaseUrl.TextChanged += (_, _) => ResetSaveValidationStatus();
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

        var previous = _settingsHolder.Snapshot();
        var orderHub = new WaslaOptions
        {
            BaseUrl = _txtBaseUrl.Text.Trim(),
            AgentToken = _txtAgentToken.Text.Trim()
        };

        var bridge = BuildBridgeOptionsFromForm(previous.Bridge);

        if (!PrintBridgeSettingsValidator.TryValidate(orderHub, bridge, out var errorKey))
        {
            MessageBox.Show(_localizer[errorKey!], PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var previousLanguage = _settingsHolder.Ui.Language;
        var ui = _settingsHolder.Ui;
        PrintBridgeWindowLayout.CaptureInto(ui, this);
        if (_cmbLanguage.SelectedItem is LanguageOption languageOption)
            ui.Language = languageOption.CultureName;

        var connectionChanged = ConnectionSettingsChanged(previous.OrderHub, orderHub);
        _settingsLogger.LogInformation(
            "Settings save started. ConnectionChanged={ConnectionChanged}",
            connectionChanged);

        if (connectionChanged)
        {
            await SaveSettingsWithConnectionValidationAsync(previous, orderHub, bridge, ui, previousLanguage)
                .ConfigureAwait(true);
            return;
        }

        PersistSettings(orderHub, bridge, ui, previousLanguage, connectionVerified: false);
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

        _settingsLogger.LogInformation("Token/server validation started.");

        try
        {
            _settingsHolder.Replace(orderHub, bridge, ui);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _runtime.ValidateConnectionAsync(cts.Token).ConfigureAwait(true);

            var syncedBridge = _settingsHolder.Snapshot().Bridge;
            var bridgeToSave = BuildBridgeOptionsFromForm(syncedBridge);
            bridgeToSave.DisplayName = syncedBridge.DisplayName;
            bridgeToSave.ServerDeviceNameResolved = syncedBridge.ServerDeviceNameResolved;

            _settingsLogger.LogInformation("Token/server validation succeeded.");
            SetSaveValidationUi(SaveValidationUiState.Verified);
            PersistSettings(orderHub, bridgeToSave, ui, previousLanguage, connectionVerified: true);
        }
        catch (Exception ex)
        {
            _settingsHolder.Replace(previous.OrderHub, previous.Bridge, previous.Ui);
            _settingsLogger.LogWarning(ex, "Token/server validation failed.");
            SetSaveValidationUi(SaveValidationUiState.Failed);
            MessageBox.Show(
                GetSaveValidationErrorMessage(ex),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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

    private void PersistSettings(
        WaslaOptions orderHub,
        PrintBridgeOptions bridge,
        UiOptions ui,
        string? previousLanguage,
        bool connectionVerified)
    {
        var previousNormalized = string.IsNullOrWhiteSpace(previousLanguage)
            ? _cultureService.CurrentCulture.Name
            : SupportedCultures.NormalizeOrDefault(previousLanguage);
        var newNormalized = SupportedCultures.NormalizeOrDefault(ui.Language);
        var languageChanged = !string.Equals(previousNormalized, newNormalized, StringComparison.OrdinalIgnoreCase);

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

            var message = connectionVerified
                ? _localizer["Message.SettingsSavedConnectionVerified"]
                : languageChanged
                    ? _localizer["Message.LanguageRestartRequired"]
                    : _localizer["Message.SettingsSaved"];

            MessageBox.Show(message, PrintBridgePaths.ProductDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            SyncDeviceNameFieldFromHolder();
            RefreshDashboard();

            if (!connectionVerified)
                SetSaveValidationUi(SaveValidationUiState.Idle);
        }
        catch (Exception ex)
        {
            _settingsLogger.LogWarning(ex, "Settings save failed.");
            MessageBox.Show(
                _localizer.GetString("Message.SettingsSaveFailed", ex.Message),
                PrintBridgePaths.ProductDisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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

    private static bool ConnectionSettingsChanged(WaslaOptions previous, WaslaOptions next) =>
        !string.Equals(NormalizeBaseUrl(previous.BaseUrl), NormalizeBaseUrl(next.BaseUrl), StringComparison.OrdinalIgnoreCase)
        || !string.Equals(previous.AgentToken.Trim(), next.AgentToken.Trim(), StringComparison.Ordinal);

    private static string NormalizeBaseUrl(string baseUrl) =>
        baseUrl.Trim().TrimEnd('/');

    private void SetConnectionFieldsEnabled(bool enabled)
    {
        _txtBaseUrl.Enabled = enabled;
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

    private string GetSaveValidationErrorMessage(Exception ex)
    {
        if (ex is PrintBridgeConnectionException connectionEx)
        {
            if (connectionEx.IsTokenAuthFailure)
                return _localizer["Message.TokenInvalidUnauthorized"];

            return connectionEx.UserMessageKey switch
            {
                "Connection.SslError" => _localizer["Message.SslCertificateError"],
                "Connection.ServerUnreachable" or "Connection.ServerUnavailable" => _localizer["Message.ServerUnreachable"],
                "Connection.EndpointNotFound" => _localizer["Message.ConnectionCouldNotBeVerified"],
                _ => _localizer["Message.ConnectionCouldNotBeVerified"]
            };
        }

        return _localizer["Message.ConnectionCouldNotBeVerified"];
    }
}
