using System.Reflection;

namespace Wasla.PrintBridge.Configuration;

public sealed class AppVersionInfo
{
    /// <param name="productAssembly">
    /// The desktop executable's assembly. The version shown to users and sent in
    /// <c>X-PrintBridge-Version</c> is the product version, not this library's version.
    /// </param>
    public AppVersionInfo(Assembly productAssembly)
    {
        var version = productAssembly.GetName().Version;
        Semantic = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        Display = $"v{Semantic}";
        HeaderValue = version?.ToString() ?? "1.0.0.0";
    }

    public string Semantic { get; }
    public string Display { get; }
    public string HeaderValue { get; }
}
