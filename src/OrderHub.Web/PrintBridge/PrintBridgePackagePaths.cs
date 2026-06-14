namespace OrderHub.Web.PrintBridge;

public static class PrintBridgePackagePaths
{
    public const string DefaultPackageFileName = "OrderHub.PrintBridge-win-x64.zip";
    public const string DefaultRelativeFolder = "downloads/orderhub-print-bridge";

    public static string GetPackageFileName(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration["OrderHub:PrintBridgeDownload:PackageFileName"])
            ? DefaultPackageFileName
            : configuration["OrderHub:PrintBridgeDownload:PackageFileName"]!.Trim();

    public static string ResolvePackagePath(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["OrderHub:PrintBridgeDownload:PackagePath"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
        }

        var fileName = GetPackageFileName(configuration);
        return Path.Combine(environment.WebRootPath, DefaultRelativeFolder.Replace('/', Path.DirectorySeparatorChar), fileName);
    }

    public static bool PackageExists(IConfiguration configuration, IWebHostEnvironment environment) =>
        File.Exists(ResolvePackagePath(configuration, environment));
}
