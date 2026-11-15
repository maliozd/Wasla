using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wasla.PrintBridge.Updates;

/// <summary>What Windows Installer says about the installed Print Bridge (WAS-63), for deciding when an update is committed.</summary>
internal interface IPrintBridgeInstalledProduct
{
    /// <summary>True while any Windows Installer installation runs on this computer (its execution mutex exists).</summary>
    bool InstallerIsRunning();

    /// <summary>The version Windows Installer has registered for the Print Bridge MSI, or null when it has none.</summary>
    Version? InstalledVersion();
}

/// <summary>
/// The installed Print Bridge MSI, found by its upgrade code (WAS-62, never changes), and Windows Installer's execution mutex
/// (<c>Global\_MSIExecute</c>), which exists exactly while an installation's transaction runs.
/// </summary>
internal sealed class PrintBridgeInstalledProduct : IPrintBridgeInstalledProduct
{
    /// <summary>The Print Bridge MSI's upgrade code (installer/print-bridge/Package/Package.wxs; checked by a test).</summary>
    public const string MsiUpgradeCode = "{2DAEE4A2-EF79-45BD-B403-602FFC1E737E}";

    private const uint Synchronize = 0x00100000;
    private const int ErrorFileNotFound = 2;

    public bool InstallerIsRunning()
    {
        using var mutex = OpenMutex(Synchronize, false, @"Global\_MSIExecute");
        if (!mutex.IsInvalid)
            return true;

        // Any reason it cannot be opened other than "it does not exist" (access denied) still means it exists.
        return Marshal.GetLastWin32Error() != ErrorFileNotFound;
    }

    public Version? InstalledVersion()
    {
        var product = new StringBuilder(39);
        if (MsiEnumRelatedProducts(MsiUpgradeCode, 0, 0, product) != 0)
            return null;

        uint length = 64;
        var value = new StringBuilder((int)length);
        return MsiGetProductInfo(product.ToString(), "VersionString", value, ref length) == 0
            ? PrintBridgeReleaseVersion.FromResource(value.ToString())
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle OpenMutex(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint productIndex, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint length);
}
