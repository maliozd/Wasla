namespace Wasla.PrintBridge.Configuration;

/// <summary>
/// Canonical ProgramData paths for Print Bridge runtime config and logs.
/// Customer-specific values must not be stored under Program Files or in the repo.
/// </summary>
public static class PrintBridgePaths
{
    public const string ServiceName = "WaslaPrintBridge";
    public const string ProductDisplayName = "Wasla Print Bridge";

    private static string? _testRootOverride;

    public static string ProgramDataRoot =>
        Volatile.Read(ref _testRootOverride)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Wasla", "PrintBridge");

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

    /// <summary>
    /// Test-only: points every Print Bridge path at <paramref name="root"/> until the returned scope is
    /// disposed, so engine tests never read or write the machine's real settings, token or history.
    /// </summary>
    internal static IDisposable UseRootForTests(string root)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("Test root must be an absolute path.", nameof(root));

        var previous = Interlocked.Exchange(ref _testRootOverride, root);
        return new RootScope(previous);
    }

    private sealed class RootScope(string? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Volatile.Write(ref _testRootOverride, previous);
        }
    }
}
