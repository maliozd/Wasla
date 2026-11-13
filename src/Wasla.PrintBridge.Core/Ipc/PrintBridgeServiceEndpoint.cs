using System.Security.Cryptography;
using System.Text;
using Wasla.PrintBridge.Configuration;

namespace Wasla.PrintBridge.Ipc;

/// <summary>
/// The names one Print Bridge engine is reached and locked by (WAS-61). The production data folder uses the production
/// names, including WAS-92's machine-wide engine lock, so an older Print Bridge app that still runs its own engine and the
/// service exclude each other. Any other data folder (a developer's, a test's) gets names derived from its path: two
/// engines can never use one folder at the same time, and a test never takes the production names.
/// </summary>
/// <param name="ServiceName">The Windows Service whose process the app verifies as the pipe's server.</param>
/// <param name="PipeName">The pipe name, without <c>\\.\pipe\</c>.</param>
/// <param name="EngineLockName">The named mutex only the running engine holds.</param>
public sealed record PrintBridgeServiceEndpoint(string ServiceName, string PipeName, string EngineLockName)
{
    public const string ProductionServiceName = PrintBridgePaths.ServiceName;
    public const string ProductionPipeName = "WaslaPrintBridge.Service";

    /// <summary>The engine lock of WAS-92 (<c>SingleInstanceGuard</c> before WAS-61), kept so older apps see the service.</summary>
    public const string ProductionEngineLockName = @"Global\WaslaPrintBridge";

    public static PrintBridgeServiceEndpoint Production { get; } =
        new(ProductionServiceName, ProductionPipeName, ProductionEngineLockName);

    public bool IsProduction => this == Production;

    /// <summary>The endpoint of the engine that uses <paramref name="dataRoot"/>, under the service name it is registered as.</summary>
    public static PrintBridgeServiceEndpoint ForDataRoot(string dataRoot, string serviceName = ProductionServiceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (PrintBridgePaths.IsProductionDataRoot(dataRoot))
            return Production with { ServiceName = serviceName };

        var suffix = Suffix(dataRoot);
        return new PrintBridgeServiceEndpoint(
            serviceName,
            $"{ProductionPipeName}.{suffix}",
            $"{ProductionEngineLockName}.{suffix}");
    }

    private static string Suffix(string dataRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
