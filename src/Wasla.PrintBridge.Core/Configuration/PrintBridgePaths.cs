namespace Wasla.PrintBridge.Configuration;

/// <summary>
/// Canonical ProgramData paths for Print Bridge runtime config and logs.
/// Customer-specific values must not be stored under Program Files or in the repo.
/// </summary>
public static class PrintBridgePaths
{
    public const string ServiceName = "WaslaPrintBridge";
    public const string ProductDisplayName = "Wasla Print Bridge";

    /// <summary>
    /// Debug builds only: an absolute directory that replaces ProgramData so a development instance
    /// runs with its own settings, token, history and logs. Ignored by Release builds.
    /// </summary>
    public const string DevelopmentDataRootVariable = "WASLA_PRINTBRIDGE_DATA_ROOT";

    private static readonly string? DevelopmentDataRoot = ResolveDevelopmentDataRoot();

    private static string? _testRootOverride;

    public static string ProgramDataRoot =>
        Volatile.Read(ref _testRootOverride)
        ?? DevelopmentDataRoot
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Wasla", "PrintBridge");

    /// <summary>
    /// True when a Debug build runs against <see cref="DevelopmentDataRootVariable"/>. Such an instance must
    /// not register machine-wide integrations (protocol handler, setup IPC) that belong to the real install.
    /// </summary>
    public static bool IsIsolatedDevelopmentRoot => DevelopmentDataRoot is not null;

    /// <summary>True while <see cref="UseRootForTests"/> is active.</summary>
    internal static bool HasTestRootOverride => Volatile.Read(ref _testRootOverride) is not null;

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

    private static string? ResolveDevelopmentDataRoot()
    {
#if DEBUG
        var value = Environment.GetEnvironmentVariable(DevelopmentDataRootVariable)?.Trim();
        return !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : null;
#else
        return null;
#endif
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
