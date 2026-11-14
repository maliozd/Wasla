using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests.Installer;

/// <summary>
/// WAS-62: who owns the <c>wasla-printbridge://</c> handler. Installed by the setup, the computer's handler serves every
/// user and the app writes no per-user copy (one would hide the installed handler and outlive an uninstall); a per-user
/// copy an earlier Print Bridge wrote is removed; another program's is left alone. Without the setup's registration (a
/// development or support copy) the app registers itself for the user as before.
/// The class roots are keys of this test under HKCU; the computer's and the user's real handlers are never touched.
/// </summary>
public sealed class PrintBridgeProtocolRegistrarTests : IDisposable
{
    private const string TestsKey = @"Software\WaslaPrintBridgeTests";
    private const string Installed = "\"C:\\Program Files\\Wasla\\Print Bridge\\Wasla.PrintBridge.exe\" \"%1\"";

    private readonly string _root = TestsKey + @"\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _machine;
    private readonly RegistryKey _user;
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("wasla-pb-protocol-");
    private readonly string _exe;

    public PrintBridgeProtocolRegistrarTests()
    {
        _machine = Registry.CurrentUser.CreateSubKey(_root + @"\machine");
        _user = Registry.CurrentUser.CreateSubKey(_root + @"\user");
        _exe = Path.Combine(_folder.FullName, "Wasla.PrintBridge.exe");
        File.WriteAllText(_exe, "stand-in");
    }

    public void Dispose()
    {
        _machine.Dispose();
        _user.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        try
        {
            // The shared parent goes once no test class uses it; another class may just have added its own key.
            Registry.CurrentUser.DeleteSubKey(TestsKey, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException)
        {
        }

        _folder.Delete(recursive: true);
    }

    [Fact]
    public void Installed_TheComputersHandlerServes_AndTheAppWritesNoPerUserCopy()
    {
        WriteHandler(_machine, Installed);

        var result = Register();

        Assert.Equal(PrintBridgeProtocolRegistration.MachineWide, result);
        Assert.Null(_user.OpenSubKey(PrintBridgeProtocolUri.Scheme));
        Assert.Equal(Installed, Command(_machine));
    }

    [Fact]
    public void Installed_APerUserCopyAnEarlierPrintBridgeWrote_IsRemoved()
    {
        WriteHandler(_machine, Installed);
        WriteHandler(_user, "\"C:\\Wasla\\PrintBridge\\Wasla.PrintBridge.exe\" \"%1\"");

        var result = Register();

        Assert.Equal(PrintBridgeProtocolRegistration.MachineWideUserCopyRemoved, result);
        Assert.Null(_user.OpenSubKey(PrintBridgeProtocolUri.Scheme));
        Assert.Equal(Installed, Command(_machine));
        // Once removed, the next start finds nothing to do.
        Assert.Equal(PrintBridgeProtocolRegistration.MachineWide, Register());
    }

    [Fact]
    public void Installed_AnotherProgramsPerUserHandler_IsLeftUnchanged()
    {
        WriteHandler(_machine, Installed);
        const string other = "\"C:\\Other\\other.exe\" \"%1\"";
        WriteHandler(_user, other);

        var result = Register();

        Assert.Equal(PrintBridgeProtocolRegistration.MachineWideHiddenByAnotherProgram, result);
        Assert.Equal(other, Command(_user));
    }

    [Fact]
    public void NotInstalled_TheAppRegistersItselfForThisUser_AsBefore()
    {
        var result = Register();

        Assert.Equal(PrintBridgeProtocolRegistration.PerUser, result);
        Assert.Equal($"\"{_exe}\" \"%1\"", Command(_user));
        using var scheme = _user.OpenSubKey(PrintBridgeProtocolUri.Scheme)!;
        Assert.Equal("URL:Wasla Print Bridge Protocol", scheme.GetValue(string.Empty));
        Assert.Equal(string.Empty, scheme.GetValue("URL Protocol"));
        using var icon = scheme.OpenSubKey("DefaultIcon")!;
        Assert.Equal($"\"{_exe}\",0", icon.GetValue(string.Empty));
        Assert.Null(_machine.OpenSubKey(PrintBridgeProtocolUri.Scheme));
    }

    [Fact]
    public void NotInstalled_WithoutAnExecutable_NothingIsRegistered()
    {
        var result = PrintBridgeProtocolRegistrar.RegisterOrRepair(
            NullLogger.Instance, Path.Combine(_folder.FullName, "missing.exe"), _machine, _user);

        Assert.Equal(PrintBridgeProtocolRegistration.Failed, result);
        Assert.Null(_user.OpenSubKey(PrintBridgeProtocolUri.Scheme));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Wasla\\Print Bridge\\Wasla.PrintBridge.exe\" \"%1\"", true)]
    [InlineData("\"D:\\Tools\\WASLA.PRINTBRIDGE.EXE\" \"%1\"", true)]
    [InlineData("C:\\Wasla\\Wasla.PrintBridge.exe \"%1\"", true)]
    [InlineData("\"C:\\Program Files\\Wasla\\Print Bridge\\Wasla.PrintBridge.Service.exe\" \"%1\"", false)]
    [InlineData("\"C:\\Other\\other.exe\" \"%1\"", false)]
    [InlineData("\"C:\\Unclosed\\Wasla.PrintBridge.exe", false)]
    [InlineData("", false)]
    public void APerUserHandler_IsPrintBridgesOnlyWhenItStartsAPrintBridgeApp(string command, bool expected)
    {
        Assert.Equal(expected, PrintBridgeProtocolRegistrar.IsPrintBridgeCommand(command));
    }

    private PrintBridgeProtocolRegistration Register() =>
        PrintBridgeProtocolRegistrar.RegisterOrRepair(NullLogger.Instance, _exe, _machine, _user);

    private static void WriteHandler(RegistryKey classes, string command)
    {
        using var scheme = classes.CreateSubKey(PrintBridgeProtocolUri.Scheme);
        scheme.SetValue(string.Empty, "URL:Wasla Print Bridge Protocol");
        scheme.SetValue("URL Protocol", string.Empty);
        using var open = scheme.CreateSubKey(@"shell\open\command");
        open.SetValue(string.Empty, command);
    }

    private static string? Command(RegistryKey classes)
    {
        using var key = classes.OpenSubKey(PrintBridgeProtocolUri.Scheme + @"\shell\open\command");
        return key?.GetValue(string.Empty) as string;
    }
}
