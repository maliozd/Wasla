using System.Reflection;

namespace Wasla.PrintBridge.Configuration;

public sealed class AppVersionInfo
{
    public AppVersionInfo()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Semantic = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        Display = $"v{Semantic}";
        HeaderValue = version?.ToString() ?? "1.0.0.0";
    }

    public string Semantic { get; }
    public string Display { get; }
    public string HeaderValue { get; }
}
