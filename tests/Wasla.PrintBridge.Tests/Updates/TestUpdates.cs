using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Updates;

namespace Wasla.PrintBridge.Tests.Updates;

/// <summary>Update coordinators for tests (WAS-63): the service's construction with test seams for the publisher and Windows Installer.</summary>
internal static class TestUpdates
{
    /// <summary>
    /// A coordinator as the service builds it. <paramref name="publisher"/> null is an unsigned installation (every test and
    /// development build): updates are refused, as in production.
    /// </summary>
    public static PrintBridgeUpdateCoordinator Coordinator(
        PrintBridgePublisher? publisher = null,
        Version? installed = null,
        IPrintBridgeInstalledProduct? product = null) =>
        new(
            new PrintBridgeUpdateStateStore(NullLogger<PrintBridgeUpdateStateStore>.Instance),
            installed ?? PrintBridgeReleaseVersion.Of(typeof(PrintBridgeServiceBackend).Assembly),
            publisher,
            product ?? new FakeInstalledProduct(),
            NullLogger<PrintBridgeUpdateCoordinator>.Instance);
}

/// <summary>Windows Installer as a test controls it: whether an installation runs, and the version it has registered.</summary>
internal sealed class FakeInstalledProduct : IPrintBridgeInstalledProduct
{
    private volatile bool _installing;
    private Version? _version;

    public bool Installing
    {
        get => _installing;
        set => _installing = value;
    }

    public Version? Version
    {
        get => Volatile.Read(ref _version);
        set => Volatile.Write(ref _version, value);
    }

    public bool InstallerIsRunning() => Installing;

    public Version? InstalledVersion() => Version;
}
