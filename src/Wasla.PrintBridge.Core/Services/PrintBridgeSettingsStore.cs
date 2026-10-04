using System.Text.Json;
using System.Text.Json.Nodes;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

/// <summary>
/// Loads and saves Print Bridge settings.
/// Repo/exe-local appsettings.json is a safe empty sample only.
/// Real customer values are entered in the tray app Settings UI and persisted under ProgramData.
/// Loading order: ProgramData (if present) → else seed from exe-local appsettings.json or defaults → ProgramData.
/// Runtime saves always write to ProgramData only.
/// </summary>
public sealed class PrintBridgeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettingsDocument Load()
    {
        SeedProgramDataConfigIfMissing();
        return ReadDocument(PrintBridgePaths.ProgramDataConfigPath);
    }

    public void Save(AppSettingsDocument document)
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        WriteDocument(PrintBridgePaths.ProgramDataConfigPath, document);
    }

    public void SaveLanguage(string cultureName)
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        SeedProgramDataConfigIfMissing();

        var path = PrintBridgePaths.ProgramDataConfigPath;
        var json = File.ReadAllText(path);
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new JsonException("Settings document root is invalid.");

        if (root["Ui"] is not JsonObject ui)
        {
            ui = new JsonObject();
            root["Ui"] = ui;
        }

        ui["Language"] = cultureName;
        WriteJson(path, root.ToJsonString(JsonOptions));
    }

    public void SeedProgramDataConfigIfMissing()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        if (File.Exists(PrintBridgePaths.ProgramDataConfigPath))
            return;

        var initial = TryLoadExeLocalConfig() ?? CreateDefaultDocument();
        NormalizeOrderHub(initial.OrderHub);
        NormalizeDeviceIdentity(initial);
        WriteDocument(PrintBridgePaths.ProgramDataConfigPath, initial);
    }

    public bool ProgramDataConfigExists() =>
        File.Exists(PrintBridgePaths.ProgramDataConfigPath);

    private static AppSettingsDocument? TryLoadExeLocalConfig()
    {
        var exeLocalPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(exeLocalPath))
            return null;

        try
        {
            return ReadDocument(exeLocalPath);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AppSettingsDocument ReadDocument(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<AppSettingsDocument>(json, JsonOptions) ?? CreateDefaultDocument();
            NormalizeOrderHub(document.OrderHub);
            NormalizeDeviceIdentity(document);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            if (string.Equals(path, PrintBridgePaths.ProgramDataConfigPath, StringComparison.OrdinalIgnoreCase))
                TryBackupCorruptedFile(path);

            return CreateDefaultDocument();
        }
    }

    private static void WriteDocument(string path, AppSettingsDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        WriteJson(path, json);
    }

    private static void WriteJson(string path, string json)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(path))
                File.Replace(tempPath, path, destinationBackupFileName: null);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void TryBackupCorruptedFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var backupPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, backupPath, overwrite: false);
        }
        catch
        {
        }
    }

    private static void NormalizeOrderHub(WaslaOptions orderHub)
    {
        if (string.IsNullOrWhiteSpace(orderHub.ServerUrl))
            orderHub.ServerUrl = WaslaOptions.DefaultServerUrl;
        else
            orderHub.ServerUrl = orderHub.ServerUrl.Trim().TrimEnd('/');

        orderHub.AgentToken = orderHub.AgentToken?.Trim() ?? string.Empty;
    }

    private static void NormalizeDeviceIdentity(AppSettingsDocument document)
    {
        var machineName = Environment.MachineName;
        document.PrintBridge.MachineName = machineName;

        if (!Guid.TryParse(document.PrintBridge.InstallationId, out var installationId) ||
            installationId == Guid.Empty)
        {
            installationId = Guid.NewGuid();
        }

        document.PrintBridge.InstallationId = installationId.ToString("D");

        if (string.IsNullOrWhiteSpace(document.PrintBridge.DisplayName)
            && !string.IsNullOrWhiteSpace(document.PrintBridge.BridgeName))
        {
            var legacy = document.PrintBridge.BridgeName.Trim();
            if (!string.Equals(legacy, machineName, StringComparison.OrdinalIgnoreCase))
                document.PrintBridge.DisplayName = legacy;
        }

        document.PrintBridge.BridgeName = string.Empty;
    }

    private static AppSettingsDocument CreateDefaultDocument() =>
        new()
        {
            OrderHub = new WaslaOptions(),
            PrintBridge = new PrintBridgeOptions { DryRun = true },
            Ui = new UiOptions()
        };

    public sealed class AppSettingsDocument
    {
        public WaslaOptions OrderHub { get; set; } = new();
        public PrintBridgeOptions PrintBridge { get; set; } = new();
        public UiOptions Ui { get; set; } = new();
    }
}
