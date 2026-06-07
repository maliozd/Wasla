namespace OrderHub.PrintBridge.Configuration;

public static class PrintBridgePaths
{
    public const string ServiceName = "OrderHubPrintBridge";
    public const string ProductDisplayName = "OrderHub Print Bridge";

    public static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OrderHub", "PrintBridge");

    public static string ProgramDataConfigPath =>
        Path.Combine(ProgramDataRoot, "appsettings.json");

    public static string ProgramDataLogDirectory =>
        Path.Combine(ProgramDataRoot, "logs");

    public static void EnsureProgramDataDirectories()
    {
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(ProgramDataLogDirectory);
    }
}
