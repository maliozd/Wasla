namespace Wasla.Web.PrintBridge;

/// <summary>
/// The Print Bridge file the setup page offers through <c>GET /print-bridge/download/package</c>. Since WAS-62 it is the
/// setup EXE built by <c>scripts/release/package-print-bridge.ps1</c>; the support ZIP is never offered to restaurants.
/// </summary>
public static class PrintBridgePackagePaths
{
    public const string DefaultPackageFileName = "Wasla.PrintBridge-Setup-x64.exe";
    public const string DefaultRelativeFolder = "downloads/wasla-print-bridge";

    /// <summary>The download's media type, by the configured file's extension (an EXE, or a ZIP for development and support).</summary>
    public static string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".exe" => "application/vnd.microsoft.portable-executable",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    public static string GetPackageFileName(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration["Wasla:PrintBridgeDownload:PackageFileName"])
            ? DefaultPackageFileName
            : configuration["Wasla:PrintBridgeDownload:PackageFileName"]!.Trim();

    /// <summary>
    /// The configured package path, otherwise the default folder in the web root. Null when no path is configured and
    /// the host has no web root (ASP.NET Core leaves <c>WebRootPath</c> null when the content root has no wwwroot,
    /// WAS-128): the package is then unavailable.
    /// </summary>
    public static string? ResolvePackagePath(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["Wasla:PrintBridgeDownload:PackagePath"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
        }

        if (string.IsNullOrEmpty(environment.WebRootPath))
            return null;

        var fileName = GetPackageFileName(configuration);
        return Path.Combine(environment.WebRootPath, DefaultRelativeFolder.Replace('/', Path.DirectorySeparatorChar), fileName);
    }

    public static bool PackageExists(IConfiguration configuration, IWebHostEnvironment environment) =>
        ResolvePackagePath(configuration, environment) is { } path && File.Exists(path);
}
