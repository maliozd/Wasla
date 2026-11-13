using System.Security.AccessControl;
using System.Security.Principal;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// WAS-61: one engine per computer. The service's engine lock, the names that follow its data folder, and how an older
/// Print Bridge app that still runs its own engine and the service exclude each other. Every test uses lock names of its
/// own, never the installed client's.
/// </summary>
public sealed class PrintBridgeEngineLockTests
{
    [Fact]
    public void TheFirstEngine_TakesTheLock_ASecondDoesNot_AndItIsFreeAgainOnceReleased()
    {
        var name = UniqueName();

        var first = PrintBridgeEngineLock.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(PrintBridgeEngineLock.TryAcquire(name));

        first.Dispose();
        using var again = PrintBridgeEngineLock.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void AnOlderAppThatRunsItsOwnEngine_KeepsTheServiceFromStartingOne()
    {
        var name = UniqueName();

        // What SingleInstanceGuard did before WAS-61: the app created the engine lock and kept it while it ran.
        using var olderApp = new Mutex(initiallyOwned: true, name, out var created);
        Assert.True(created);

        Assert.Null(PrintBridgeEngineLock.TryAcquire(name));
    }

    [Fact]
    public void WhileTheServiceRuns_AnOlderAppNeverSeesTheLockAsFree()
    {
        var name = UniqueName();
        using var service = PrintBridgeEngineLock.TryAcquire(name);
        Assert.NotNull(service);

        // The older app's own check: the lock counts as taken only when it created it. The service's access rules may
        // also refuse to open it; either way the older app starts no engine.
        var olderAppCreatedIt = false;
        try
        {
            using var attempt = new Mutex(initiallyOwned: true, name, out olderAppCreatedIt);
        }
        catch (UnauthorizedAccessException)
        {
        }

        Assert.False(olderAppCreatedIt);
    }

    [Fact]
    public void ALockThatCannotBeOpened_OrAnotherKindOfObject_IsNeverTreatedAsFree()
    {
        var denied = UniqueName();
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Deny));
        using (MutexAcl.Create(initiallyOwned: false, denied, out var created, security))
        {
            Assert.True(created);
            Assert.Null(PrintBridgeEngineLock.TryAcquire(denied));
        }

        var other = UniqueName();
        using (new EventWaitHandle(false, EventResetMode.ManualReset, other))
            Assert.Null(PrintBridgeEngineLock.TryAcquire(other));
    }

    [Fact]
    public void TheProductionDataFolder_UsesTheProductionNames()
    {
        var endpoint = PrintBridgeServiceEndpoint.ForDataRoot(PrintBridgePaths.ProductionDataRoot);

        Assert.Equal(PrintBridgeServiceEndpoint.Production, endpoint);
        Assert.True(endpoint.IsProduction);
        Assert.Equal(@"Global\WaslaPrintBridge", endpoint.EngineLockName);
        Assert.Equal("WaslaPrintBridge.Service", endpoint.PipeName);
        Assert.Equal("WaslaPrintBridge", endpoint.ServiceName);
        // Spelled differently, it is the same folder.
        Assert.Equal(endpoint, PrintBridgeServiceEndpoint.ForDataRoot(PrintBridgePaths.ProductionDataRoot.ToUpperInvariant() + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AnyOtherDataFolder_GetsNamesOfItsOwn_TheSameEveryTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "wasla-pb-endpoint-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(Path.GetTempPath(), "wasla-pb-endpoint-" + Guid.NewGuid().ToString("N"));

        var endpoint = PrintBridgeServiceEndpoint.ForDataRoot(root, "WaslaPbTest");

        Assert.False(endpoint.IsProduction);
        Assert.Equal("WaslaPbTest", endpoint.ServiceName);
        Assert.StartsWith(@"Global\WaslaPrintBridge.", endpoint.EngineLockName, StringComparison.Ordinal);
        Assert.StartsWith("WaslaPrintBridge.Service.", endpoint.PipeName, StringComparison.Ordinal);
        Assert.NotEqual(PrintBridgeServiceEndpoint.ProductionEngineLockName, endpoint.EngineLockName);
        Assert.Equal(endpoint, PrintBridgeServiceEndpoint.ForDataRoot(root.ToUpperInvariant(), "WaslaPbTest"));
        Assert.NotEqual(endpoint.EngineLockName, PrintBridgeServiceEndpoint.ForDataRoot(other).EngineLockName);
        Assert.NotEqual(endpoint.PipeName, PrintBridgeServiceEndpoint.ForDataRoot(other).PipeName);
    }

    [Theory]
    [InlineData(new string[0], false, "WaslaPrintBridge", null)]
    [InlineData(new[] { "secure-data" }, true, "WaslaPrintBridge", null)]
    [InlineData(new[] { "--service-name", "WaslaPbTest_1", "--data-root", @"C:\Temp\pb" }, false, "WaslaPbTest_1", @"C:\Temp\pb")]
    [InlineData(new[] { "secure-data", "--service-name", "WaslaPbTest_1", "--data-root", @"C:\Temp\pb\" }, true, "WaslaPbTest_1", @"C:\Temp\pb")]
    public void TheCommandLine_IsParsedStrictly(string[] args, bool secureData, string serviceName, string? dataRoot)
    {
        Assert.True(PrintBridgeServiceOptions.TryParse(args, out var options, out var error));
        Assert.Null(error);
        Assert.Equal(secureData, options!.SecureDataOnly);
        Assert.Equal(serviceName, options.ServiceName);
        Assert.Equal(dataRoot, options.DataRoot);
    }

    [Theory]
    [InlineData("--data-root")]
    [InlineData("--data-root", "relative\\path")]
    [InlineData("--service-name", "bad name")]
    [InlineData("--service-name", "a\"b")]
    [InlineData("--verbose")]
    [InlineData("--data-root", @"C:\Temp\pb", "secure-data")]
    public void AnythingElseOnTheCommandLine_IsRefused(params string[] args)
    {
        Assert.False(PrintBridgeServiceOptions.TryParse(args, out var options, out var error));
        Assert.Null(options);
        Assert.NotNull(error);
    }

    private static string UniqueName() => $@"Local\WaslaPbTest.Engine.{Guid.NewGuid():N}";
}
