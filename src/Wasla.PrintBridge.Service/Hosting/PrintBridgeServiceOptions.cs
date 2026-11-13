using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Ipc;

namespace Wasla.PrintBridge.Hosting;

/// <summary>
/// The service's command line (WAS-61):
/// <code>
/// Wasla.PrintBridge.Service.exe [secure-data] [--service-name &lt;name&gt;] [--data-root &lt;absolute path&gt;]
/// </code>
/// Without options it is the production service: <c>WaslaPrintBridge</c> on <c>C:\ProgramData\Wasla\PrintBridge</c>.
/// <c>--data-root</c> selects another folder, whose engine lock and pipe are derived from it
/// (<see cref="PrintBridgeServiceEndpoint.ForDataRoot"/>); only the Service Control Manager's configuration (owned by
/// administrators) or a developer's console passes it. <c>secure-data</c> applies the folder's access rules and exits.
/// </summary>
internal sealed record PrintBridgeServiceOptions(
    bool SecureDataOnly,
    string ServiceName,
    string? DataRoot)
{
    public static bool TryParse(IReadOnlyList<string> args, out PrintBridgeServiceOptions? options, out string? error)
    {
        options = null;
        error = null;
        var secureData = false;
        var serviceName = PrintBridgeServiceEndpoint.ProductionServiceName;
        string? dataRoot = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "secure-data" when i == 0:
                    secureData = true;
                    break;
                case "--service-name" when i + 1 < args.Count && IsServiceName(args[i + 1]):
                    serviceName = args[++i];
                    break;
                case "--data-root" when i + 1 < args.Count && Path.IsPathFullyQualified(args[i + 1]):
                    dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[++i]));
                    break;
                default:
                    error = "Usage: Wasla.PrintBridge.Service.exe [secure-data] [--service-name <name>] [--data-root <absolute path>]";
                    return false;
            }
        }

        options = new PrintBridgeServiceOptions(secureData, serviceName, dataRoot);
        return true;
    }

    /// <summary>The data folder this process uses (after <see cref="PrintBridgePaths.ConfigureDataRoot"/>).</summary>
    public string EffectiveDataRoot => PrintBridgePaths.ProgramDataRoot;

    public PrintBridgeServiceEndpoint Endpoint => PrintBridgeServiceEndpoint.ForDataRoot(EffectiveDataRoot, ServiceName);

    private static bool IsServiceName(string value) =>
        value.Length is > 0 and <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
