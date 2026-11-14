using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Ipc;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Tests.Updates;
using Wasla.PrintBridge.Tests.WebShell;
using Wasla.PrintBridge.Updates;

namespace Wasla.PrintBridge.Tests.ServiceClient;

/// <summary>
/// WAS-63: the production <see cref="PrintBridgeServiceClient"/> is the app's <see cref="IPrintBridgeUpdates"/>, over a real
/// pipe to the production server with the service's update coordinator (an unsigned installation, as every test build is).
/// The update state arrives with every service state and is gone while the service cannot be reached; the commands reach
/// the service and its refusals come back as resource keys the app localizes.
/// </summary>
public sealed class PrintBridgeServiceClientUpdatesTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _pipeName = "WaslaPbTest." + Guid.NewGuid().ToString("N");
    private readonly PrintBridgeServiceBackend _backend = new(TestUpdates.Coordinator());
    private readonly FakeStatusSource _engine = new();
    private PrintBridgeIpcServer? _server;
    private PrintBridgeServiceClient? _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_server is not null)
            await _server.DisposeAsync();
        await _backend.Updates.DisposeAsync();
    }

    [Fact]
    public async Task TheUpdateState_ComesWithEveryServiceState_AndIsGoneWithoutTheService()
    {
        IPrintBridgeUpdates updates = _client = NewClient();
        Assert.Null(updates.CurrentUpdate);

        await StartServerAsync();
        await _client.Connected.WaitAsync(Wait, Ct);

        var update = Assert.IsType<IpcUpdate>(updates.CurrentUpdate);
        Assert.Equal(PrintBridgeReleaseVersion.Format(_backend.Updates.InstalledVersion), update.InstalledVersion);
        Assert.False(update.Supported);
        Assert.Null(update.AttemptId);
        Assert.Null(update.Stage);
        Assert.Null(update.StagedSetupPath);

        await _server!.StopAsync();
        await WaitUntilAsync(() => updates.CurrentUpdate is null);
    }

    [Fact]
    public async Task TheCommands_ReachTheService_AndItsRefusalsComeBackAsResourceKeys()
    {
        await StartServerAsync();
        IPrintBridgeUpdates updates = _client = NewClient();
        await _client.Connected.WaitAsync(Wait, Ct);

        var check = await Assert.ThrowsAsync<LocalizedApplicationException>(() => updates.CheckForUpdateAsync(Ct));
        var prepare = await Assert.ThrowsAsync<LocalizedApplicationException>(() => updates.PrepareUpdateAsync("9.9.9", Ct));
        Assert.Equal("Update.Error.NotSigned", check.ResourceKey);
        Assert.Equal("Update.Error.NotSigned", prepare.ResourceKey);

        // A version in another form is refused by the service's validation, not interpreted.
        await Assert.ThrowsAsync<PrintBridgeServiceException>(() => updates.PrepareUpdateAsync("9.9", Ct));

        // Cancelling an attempt that is not running changes nothing and is not an error.
        await updates.CancelUpdateAsync(Guid.NewGuid(), Ct);
        Assert.Null(updates.CurrentUpdate?.AttemptId);
    }

    [Fact]
    public async Task WithoutTheService_TheCommandsSaySoInTheUsersLanguage()
    {
        IPrintBridgeUpdates updates = _client = NewClient();

        var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(() => updates.CheckForUpdateAsync(Ct));

        Assert.Equal(new Wasla.PrintBridge.Models.PrintBridgeRuntimeIssue(Wasla.PrintBridge.Models.PrintBridgeRuntimeIssueCode.ServiceUnavailable).EffectiveDetailResourceKey, refused.ResourceKey);
    }

    private PrintBridgeServiceClient NewClient()
    {
        var client = new PrintBridgeServiceClient(
            new PrintBridgeServiceClientOptions(_pipeName, new CurrentUserServerVerifier()),
            NullLogger.Instance);
        client.Start();
        return client;
    }

    private async Task StartServerAsync()
    {
        _server = new PrintBridgeIpcServer(_pipeName, _backend, new WindowsTokenClientAuthorizer(), NullLoggerFactory.Instance, "v-test");
        _server.Start();
        await _server.Listening.WaitAsync(Wait, Ct);
        _backend.Activate(_engine);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(25, Ct);
        }
    }
}
