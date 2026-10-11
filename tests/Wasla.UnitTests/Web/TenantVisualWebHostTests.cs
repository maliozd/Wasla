using System.Net;
using Wasla.Infrastructure.Security;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The visual host must not depend on the machine it runs on. CI has no <c>ENCRYPTION_MASTER_KEY</c>, and the Orders
/// pages failed there with an empty HTTP 500 while they passed on a developer machine that sets one (WAS-74). The test
/// removes the variable for the whole test process, so the collection never runs in parallel with other tests.
/// </summary>
[Collection(nameof(MachineEncryptionKeyCollection))]
public sealed class TenantVisualWebHostTests
{
    [Fact]
    public async Task OrdersPages_AreServed_WithoutAMachineEncryptionKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var previous = Environment.GetEnvironmentVariable(AesSecretManager.MasterKeyEnvVarName);
        Environment.SetEnvironmentVariable(AesSecretManager.MasterKeyEnvVarName, null);
        try
        {
            await using var host = await TenantVisualWebHost.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
            {
                BaseAddress = host.BaseAddress
            };
            (await client.GetAsync(TenantVisualWebHost.SignInPath, ct)).EnsureSuccessStatusCode();

            foreach (var path in new[] { "/orders", "/orders/live-display", "/orders/order-settings", "/orders/sync-settings" })
            {
                using var response = await client.GetAsync(path, ct);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: HTTP {(int)response.StatusCode}; {host.ServerErrors}");
            }

            Assert.Null(host.ServerErrors);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AesSecretManager.MasterKeyEnvVarName, previous);
        }
    }
}

[CollectionDefinition(nameof(MachineEncryptionKeyCollection), DisableParallelization = true)]
public sealed class MachineEncryptionKeyCollection;
