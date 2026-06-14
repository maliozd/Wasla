namespace Wasla.PrintBridge.Configuration;

/// <summary>
/// Canonical ProgramData paths for Print Bridge runtime config and logs.
/// Customer-specific values must not be stored under Program Files or in the repo.
/// </summary>
public static class PrintBridgePaths
{
    public const string ServiceName = "WaslaPrintBridge";
    public const string ProductDisplayName = "Wasla Print Bridge";

    public static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Wasla", "PrintBridge");

    public static string ProgramDataConfigPath =>
        Path.Combine(ProgramDataRoot, "appsettings.json");

    public static string ProgramDataLogDirectory =>
        Path.Combine(ProgramDataRoot, "logs");

    public static string ProgramDataHistoryPath =>
        Path.Combine(ProgramDataRoot, "print-history.json");

    public static void EnsureProgramDataDirectories()
    {
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(ProgramDataLogDirectory);
    }
}
