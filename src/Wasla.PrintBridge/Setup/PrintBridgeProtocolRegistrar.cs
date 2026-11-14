using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Wasla.PrintBridge.Setup;

/// <summary>What <see cref="PrintBridgeProtocolRegistrar.RegisterOrRepair(ILogger)"/> found and did.</summary>
public enum PrintBridgeProtocolRegistration
{
    /// <summary>The installer registered the handler for the computer; no per-user copy exists.</summary>
    MachineWide,

    /// <summary>The installer registered the handler for the computer; the per-user copy an earlier Print Bridge wrote was removed.</summary>
    MachineWideUserCopyRemoved,

    /// <summary>The installer registered the handler for the computer, but another program's per-user handler hides it for this user.</summary>
    MachineWideHiddenByAnotherProgram,

    /// <summary>No installer registration (a development or support copy): the per-user handler points at this executable.</summary>
    PerUser,

    /// <summary>Nothing could be registered or checked (logged).</summary>
    Failed
}

/// <summary>
/// The <c>wasla-printbridge://</c> handler, the only scheme Print Bridge registers.
/// <para>
/// Installed by the setup (WAS-62), the handler is the installer's, for every user of the computer
/// (<c>HKLM\Software\Classes</c>), and it is removed on uninstall. The app then writes nothing: a per-user copy under
/// <c>HKCU\Software\Classes</c> would hide the computer's handler from this user (HKCU wins) and would outlive an
/// uninstall, pointing at a deleted file. A per-user copy that an earlier Print Bridge wrote (a portable copy before the
/// installer) is removed; a per-user handler of another program is left alone and logged.
/// </para>
/// <para>
/// Without the installer's registration (a development or support copy), the app registers or repairs the per-user
/// handler on each launch as before; that needs no administrator rights.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrintBridgeProtocolRegistrar
{
    private const string FriendlyName = "URL:Wasla Print Bridge Protocol";
    private const string ClassesKey = @"Software\Classes";
    private const string AppFileName = "Wasla.PrintBridge.exe";

    public static PrintBridgeProtocolRegistration RegisterOrRepair(ILogger logger)
    {
        try
        {
            using var machineClasses = Registry.LocalMachine.OpenSubKey(ClassesKey);
            using var userClasses = Registry.CurrentUser.CreateSubKey(ClassesKey)
                ?? throw new InvalidOperationException("Unable to open HKCU\\Software\\Classes.");
            return RegisterOrRepair(logger, Environment.ProcessPath, machineClasses, userClasses);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check the wasla-printbridge protocol handler.");
            return PrintBridgeProtocolRegistration.Failed;
        }
    }

    /// <summary>The decision, over given class roots (the computer's and this user's); tests pass keys of their own.</summary>
    internal static PrintBridgeProtocolRegistration RegisterOrRepair(
        ILogger logger,
        string? exePath,
        RegistryKey? machineClasses,
        RegistryKey userClasses)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ReadCommand(machineClasses)))
            {
                var userCommand = ReadCommand(userClasses);
                if (userCommand is null)
                    return PrintBridgeProtocolRegistration.MachineWide;

                if (!IsPrintBridgeCommand(userCommand))
                {
                    logger.LogWarning("A per-user wasla-printbridge handler of another program hides the installed one for this user; it was left unchanged.");
                    return PrintBridgeProtocolRegistration.MachineWideHiddenByAnotherProgram;
                }

                userClasses.DeleteSubKeyTree(PrintBridgeProtocolUri.Scheme, throwOnMissingSubKey: false);
                logger.LogInformation("Removed the per-user wasla-printbridge handler; the installed handler serves every user.");
                return PrintBridgeProtocolRegistration.MachineWideUserCopyRemoved;
            }

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                logger.LogWarning("Skipping protocol registration: executable path could not be resolved.");
                return PrintBridgeProtocolRegistration.Failed;
            }

            var command = $"\"{exePath}\" \"%1\"";
            using var schemeKey = userClasses.CreateSubKey(PrintBridgeProtocolUri.Scheme);

            schemeKey.SetValue(string.Empty, FriendlyName);
            schemeKey.SetValue("URL Protocol", string.Empty);

            using (var iconKey = schemeKey.CreateSubKey("DefaultIcon"))
                iconKey.SetValue(string.Empty, $"\"{exePath}\",0");

            using var commandKey = schemeKey.CreateSubKey(@"shell\open\command");
            var existing = commandKey.GetValue(string.Empty) as string;
            if (!string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
            {
                commandKey.SetValue(string.Empty, command);
                logger.LogInformation("Registered wasla-printbridge protocol handler for the current user.");
            }

            return PrintBridgeProtocolRegistration.PerUser;
        }
        catch (Exception ex)
        {
            // Registration is best-effort; manual setup remains available.
            logger.LogWarning(ex, "Failed to register the wasla-printbridge protocol handler.");
            return PrintBridgeProtocolRegistration.Failed;
        }
    }

    private static string? ReadCommand(RegistryKey? classes)
    {
        if (classes is null)
            return null;
        using var command = classes.OpenSubKey(PrintBridgeProtocolUri.Scheme + @"\shell\open\command");
        return command?.GetValue(string.Empty) as string;
    }

    /// <summary>A command whose program (the first, quoted or unquoted, token) is a Print Bridge app executable.</summary>
    internal static bool IsPrintBridgeCommand(string command)
    {
        var text = command.Trim();
        string program;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
                return false;
            program = text[1..end];
        }
        else
        {
            var space = text.IndexOf(' ');
            program = space < 0 ? text : text[..space];
        }

        try
        {
            return string.Equals(Path.GetFileName(program), AppFileName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
