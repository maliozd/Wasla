using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge;

/// <summary>
/// <c>Wasla.PrintBridge.Service.exe</c> (WAS-61). Under the Service Control Manager it is the Print Bridge service; with
/// <c>secure-data</c> it applies the data folder's access rules (as an administrator) and exits; as a console program it
/// runs only with a data folder of its own (<c>--data-root</c>, or a Debug build's <c>WASLA_PRINTBRIDGE_DATA_ROOT</c>),
/// never on the installed one. See <see cref="PrintBridgeServiceOptions"/>.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return 2;

        if (!PrintBridgeServiceOptions.TryParse(args, out var options, out var error) || options is null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        if (options.DataRoot is not null)
            PrintBridgePaths.ConfigureDataRoot(options.DataRoot);

        if (options.SecureDataOnly)
            return SecureData(options);

        var isService = WindowsServiceHelpers.IsWindowsService();
        if (!isService && options.DataRoot is null && !PrintBridgePaths.IsIsolatedDevelopmentRoot)
        {
            Console.Error.WriteLine(
                "Wasla Print Bridge runs as the Windows Service \"WaslaPrintBridge\". A console run needs a data folder of its own: --data-root <absolute path>.");
            return 2;
        }

        PrintBridgeAppServices.ConfigureLogging();
        try
        {
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(Log.Logger, dispose: false);
            builder.Services.AddWindowsService(o => o.ServiceName = options.ServiceName);
            // The engine waits up to 10 s for a claimed job (WAS-56); stopping must not be cut shorter than that.
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<PrintBridgeServiceBackend>();
            builder.Services.AddSingleton<IPrintBridgeIpcClientAuthorizer, WindowsTokenClientAuthorizer>();
            builder.Services.AddSingleton(sp => new PrintBridgeIpcServer(
                options.Endpoint.PipeName,
                sp.GetRequiredService<PrintBridgeServiceBackend>(),
                sp.GetRequiredService<IPrintBridgeIpcClientAuthorizer>(),
                sp.GetRequiredService<ILoggerFactory>(),
                new AppVersionInfo(typeof(Program).Assembly).Display));
            builder.Services.AddHostedService(sp => new PrintBridgeServiceWorker(
                options,
                sp.GetRequiredService<PrintBridgeServiceBackend>(),
                sp.GetRequiredService<PrintBridgeIpcServer>(),
                sp.GetRequiredService<ILogger<PrintBridgeServiceWorker>>(),
                verifyDataFolder: isService,
                PrintBridgeAppServices.Build));

            using var host = builder.Build();
            host.Run();
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "The Print Bridge service could not run.");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int SecureData(PrintBridgeServiceOptions options)
    {
        try
        {
            var account = new NTAccount("NT SERVICE", options.ServiceName);
            var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            var root = PrintBridgePaths.ProgramDataRoot;
            PrintBridgeDataFolderSecurity.Apply(root, sid);
            Console.WriteLine($"The data folder is protected for {account.Value}.");
            return 0;
        }
        catch (IdentityNotMappedException)
        {
            Console.Error.WriteLine($"The service \"{options.ServiceName}\" is not registered; run sc.exe create first.");
            return 1;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            Console.Error.WriteLine($"The data folder could not be protected ({ex.GetType().Name}). Run this as an administrator.");
            return 1;
        }
    }
}
