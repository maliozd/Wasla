namespace Wasla.Web.PrintBridge;

public static class PrintBridgePackagePaths
{
    public const string DefaultPackageFileName = "Wasla.PrintBridge-win-x64.zip";
    public const string DefaultRelativeFolder = "downloads/wasla-print-bridge";

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
