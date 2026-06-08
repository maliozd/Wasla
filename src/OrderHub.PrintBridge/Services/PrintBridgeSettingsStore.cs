using System.Text.Json;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Options;

namespace OrderHub.PrintBridge.Services;

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

    public void SeedProgramDataConfigIfMissing()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        if (File.Exists(PrintBridgePaths.ProgramDataConfigPath))
            return;

        var initial = TryLoadExeLocalConfig() ?? CreateDefaultDocument();
        NormalizeBridgeName(initial);
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
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettingsDocument>(json, JsonOptions) ?? CreateDefaultDocument();
    }

    private static void WriteDocument(string path, AppSettingsDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static void NormalizeBridgeName(AppSettingsDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.PrintBridge.BridgeName))
            document.PrintBridge.BridgeName = Environment.MachineName;
    }

    private static AppSettingsDocument CreateDefaultDocument() =>
        new()
        {
            OrderHub = new OrderHubOptions(),
            PrintBridge = new PrintBridgeOptions { DryRun = true },
            Ui = new UiOptions()
        };

    public sealed class AppSettingsDocument
    {
        public OrderHubOptions OrderHub { get; set; } = new();
        public PrintBridgeOptions PrintBridge { get; set; } = new();
        public UiOptions Ui { get; set; } = new();
    }
}
