using Microsoft.AspNetCore.DataProtection;

namespace Wasla.Api.Security;

/// <summary>
/// The Api's Data Protection key ring: application name <c>Wasla</c>, keys persisted to <c>DataProtection:KeyPath</c>.
/// When that key is unset, Windows uses <see cref="DefaultWindowsKeyPath"/> and every other OS keeps the framework's
/// default key storage.
/// </summary>
public static class ApiDataProtection
{
    public const string ApplicationName = "Wasla";
    public const string KeyPathConfigurationKey = "DataProtection:KeyPath";
    public const string DefaultWindowsKeyPath = @"C:\Wasla-keys";

    /// <summary>The key directory for this configuration and OS, or null for the framework's default key storage.</summary>
    public static string? ResolveKeyPath(IConfiguration configuration, bool isWindows)
    {
        var configured = configuration[KeyPathConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return isWindows ? DefaultWindowsKeyPath : null;
    }

    public static IServiceCollection AddWaslaApiDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isWindows)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName(ApplicationName);

        var keyPath = ResolveKeyPath(configuration, isWindows);
        if (keyPath is null)
            return services;

        try
        {
            var keyDir = new DirectoryInfo(keyPath);
            if (!keyDir.Exists)
                Directory.CreateDirectory(keyDir.FullName);

            dataProtection.PersistKeysToFileSystem(keyDir);
        }
        catch
        {
            // Fallback gracefully: if key persistence fails, default DP settings apply.
        }

        return services;
    }
}
