using System.Text.Json;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Options;

namespace OrderHub.PrintBridge.Services;

public sealed class PrintBridgeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettingsDocument Load()
    {
        var path = ResolveConfigPath();
        if (!File.Exists(path))
            return CreateDefaultDocument();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettingsDocument>(json, JsonOptions) ?? CreateDefaultDocument();
    }

    public void Save(AppSettingsDocument document)
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        var path = PrintBridgePaths.ProgramDataConfigPath;
        var json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void EnsureProgramDataConfigExists()
    {
        PrintBridgePaths.EnsureProgramDataDirectories();
        if (File.Exists(PrintBridgePaths.ProgramDataConfigPath))
            return;

        Save(CreateDefaultDocument());
    }

    private static string ResolveConfigPath() =>
        File.Exists(PrintBridgePaths.ProgramDataConfigPath)
            ? PrintBridgePaths.ProgramDataConfigPath
            : Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static AppSettingsDocument CreateDefaultDocument() =>
        new()
        {
            OrderHub = new OrderHubOptions(),
            PrintBridge = new PrintBridgeOptions { DryRun = true }
        };

    public sealed class AppSettingsDocument
    {
        public OrderHubOptions OrderHub { get; set; } = new();
        public PrintBridgeOptions PrintBridge { get; set; } = new();
    }
}
