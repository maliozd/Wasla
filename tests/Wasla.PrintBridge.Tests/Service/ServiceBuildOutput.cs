using Wasla.PrintBridge.Tests.WebShell;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// The service as it is built, in its own output folder. A copy of <c>Wasla.PrintBridge.Service.exe</c> also lands next to
/// the tests, but it cannot run there: the test project uses the ASP.NET Core shared framework, so the
/// <c>Microsoft.Extensions.*</c> assemblies the service ships with are not copied into the test output.
/// </summary>
internal static class ServiceBuildOutput
{
    public static string Directory
    {
        get
        {
            var configuration = AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            return Path.Combine(
                WebShellTestSupport.FindRepositoryRoot(), "src", "Wasla.PrintBridge.Service", "bin", configuration, "net8.0-windows");
        }
    }

    public static string Executable
    {
        get
        {
            var exe = Path.Combine(Directory, "Wasla.PrintBridge.Service.exe");
            Assert.True(File.Exists(exe), $"The service is built ({exe}).");
            return exe;
        }
    }
}
