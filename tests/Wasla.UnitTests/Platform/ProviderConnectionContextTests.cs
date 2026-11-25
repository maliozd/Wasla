using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Platform;
using Wasla.Integrations.Platform;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The provider client inputs of WAS-231 and Orders' adapter for them: the context carries a loaded connection's
/// values and a credential source but no credential, the source decrypts what was loaded on every call without reading
/// the database or keeping plaintext, and no text form shows a credential, a stored credential, the executor or a
/// stored payload. Fake credentials and SQLite only.
/// </summary>
public sealed class ProviderConnectionContextTests : IDisposable
{
    private const string KeyCanary = "CANARY-CIPHER-KEY-7f3a";
    private const string SecretCanary = "CANARY-CIPHER-SECRET-91c2";
    private const string PlainPrefix = "CANARY-PLAIN:";
    private const string ExecutorCanary = "canary-executor-5d8e@example.invalid";
    private const string PayloadCanary = "CANARY-CUSTOMER-PHONE-0e44";

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly RecordingSecrets _secrets = new();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenants.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToProviderContext_CarriesEveryConnectionValue_AndResolvesItsStoredCredentials(bool isActive)
    {
        var connection = Connection(isActive);

        var context = connection.ToProviderContext(_secrets);

        Assert.Equal(connection.Id, context.ConnectionId);
        Assert.Equal(ProviderPlatform.TrendyolYemek, context.Platform);
        Assert.Equal("store-a", context.StoreId);
        Assert.Equal("supplier-a", context.SupplierId);
        Assert.Equal(ExecutorCanary, context.ExecutorEmail);
        Assert.Equal(isActive, context.IsActive);
        Assert.Empty(_secrets.Calls);

        var credentials = await context.Credentials.GetCredentialsAsync(Ct);

        Assert.Equal(PlainPrefix + KeyCanary, credentials.ApiKey);
        Assert.Equal(PlainPrefix + SecretCanary, credentials.ApiSecret);
        Assert.Equal([(KeyCanary, 4), (SecretCanary, 4)], _secrets.Calls.Select(c => (c.Cipher, c.Version)).ToArray());
    }

    [Fact]
    public void ToProviderContext_KeepsTheOptionalValuesAbsent()
    {
        var connection = Connection(isActive: true);
        connection.SupplierId = null;
        connection.ExecutorEmail = null;

        var context = connection.ToProviderContext(_secrets);

        Assert.Null(context.SupplierId);
        Assert.Null(context.ExecutorEmail);
    }

    [Fact]
    public async Task CredentialSource_DecryptsOnEveryCall_WithTheCallersToken_AndKeepsNothing()
    {
        var context = Connection(isActive: true).ToProviderContext(_secrets);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        var a = await context.Credentials.GetCredentialsAsync(first.Token);
        var b = await context.Credentials.GetCredentialsAsync(second.Token);

        Assert.NotSame(a, b);
        Assert.Equal(4, _secrets.Calls.Count);
        Assert.Equal([first.Token, first.Token, second.Token, second.Token], _secrets.Calls.Select(c => c.Token).ToArray());
    }

    [Fact]
    public async Task CredentialSource_UsesTheCredentialsAsLoaded_WithoutReadingTheDatabaseAgain()
    {
        await using (var seed = await _tenants.CreateAsync(_tenant, Ct))
        {
            seed.PlatformConnections.Add(Connection(isActive: true));
            await seed.SaveChangesAsync(Ct);
        }

        ProviderConnectionContext context;
        PlatformConnection loaded;
        await using (var db = await _tenants.CreateAsync(_tenant, Ct))
        {
            loaded = await db.PlatformConnections.SingleAsync(Ct);
            context = loaded.ToProviderContext(_secrets);
        }

        // Saved by another request, and changed on the loaded entity: neither reaches this operation's context.
        await using (var edit = await _tenants.CreateAsync(_tenant, Ct))
        {
            var stored = await edit.PlatformConnections.SingleAsync(Ct);
            stored.EncryptedApiKey = "edited-key";
            stored.EncryptedApiSecret = "edited-secret";
            stored.EncryptionKeyVersion = 9;
            stored.StoreId = "edited-store";
            await edit.SaveChangesAsync(Ct);
        }

        loaded.EncryptedApiKey = "changed-in-memory";
        loaded.StoreId = "changed-in-memory";
        var statements = _tenants.StatementCount;

        var credentials = await context.Credentials.GetCredentialsAsync(Ct);

        Assert.Equal(statements, _tenants.StatementCount);
        Assert.Equal(PlainPrefix + KeyCanary, credentials.ApiKey);
        Assert.Equal(PlainPrefix + SecretCanary, credentials.ApiSecret);
        Assert.Equal([(KeyCanary, 4), (SecretCanary, 4)], _secrets.Calls.Select(c => (c.Cipher, c.Version)).ToArray());
        Assert.Equal("store-a", context.StoreId);
    }

    [Fact]
    public async Task TextForms_ShowNoCredential_NoStoredCredential_NoExecutor_AndNoStoredPayload()
    {
        var context = Connection(isActive: true).ToProviderContext(_secrets);
        var credentials = await context.Credentials.GetCredentialsAsync(Ct);
        var order = new ProviderOrderReference("pkg-1", """{"id":"pkg-1","address":{"phone":""" + "\"" + PayloadCanary + "\"}}");

        var texts = new[]
        {
            context.ToString(),
            $"{context}",
            context.Credentials.ToString()!,
            credentials.ToString(),
            $"{credentials}",
            order.ToString(),
            $"{order}"
        };

        foreach (var text in texts)
        {
            Assert.DoesNotContain(KeyCanary, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretCanary, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PlainPrefix, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ExecutorCanary, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PayloadCanary, text, StringComparison.Ordinal);
        }

        Assert.Contains(context.ConnectionId.ToString(), context.ToString(), StringComparison.Ordinal);
        Assert.Contains("pkg-1", order.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Constructors_RefuseMissingValues()
    {
        var source = Connection(isActive: true).ToProviderContext(_secrets).Credentials;

        Assert.Throws<ArgumentNullException>(() =>
            new ProviderConnectionContext(Guid.NewGuid(), ProviderPlatform.TrendyolYemek, "store", null, null, true, null!));
        Assert.Throws<ArgumentNullException>(() =>
            new ProviderConnectionContext(Guid.NewGuid(), ProviderPlatform.TrendyolYemek, null!, null, null, true, source));
        Assert.Throws<ArgumentNullException>(() => new ProviderOrderReference(null!, "{}"));
        Assert.Throws<ArgumentNullException>(() => new ProviderOrderReference("pkg-1", null!));
        Assert.Throws<ArgumentNullException>(() => ((PlatformConnection)null!).ToProviderContext(_secrets));
        Assert.Throws<ArgumentNullException>(() => Connection(isActive: true).ToProviderContext(null!));
    }

    [Fact]
    public void FoodPlatformClientContract_TakesNoOrdersPersistenceEntity()
    {
        var entityNamespace = typeof(PlatformConnection).Namespace;
        var parameterTypes = typeof(IFoodPlatformClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(method => method.GetParameters().Select(parameter => (Method: method.Name, parameter.ParameterType)))
            .ToList();

        Assert.NotEmpty(parameterTypes);
        Assert.All(parameterTypes, p => Assert.NotEqual(entityNamespace, p.ParameterType.Namespace));
        Assert.Contains(parameterTypes, p => p.ParameterType == typeof(ProviderConnectionContext));
        Assert.Contains(parameterTypes, p => p.Method == nameof(IFoodPlatformClient.RejectOrderAsync) && p.ParameterType == typeof(ProviderOrderReference));
    }

    private static PlatformConnection Connection(bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        Platform = FoodPlatform.TrendyolYemek,
        StoreId = "store-a",
        SupplierId = "supplier-a",
        ExecutorEmail = ExecutorCanary,
        EncryptedApiKey = KeyCanary,
        EncryptedApiSecret = SecretCanary,
        EncryptionKeyVersion = 4,
        IsActive = isActive,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class RecordingSecrets : ISecretManager
    {
        private readonly ConcurrentQueue<(string Cipher, int Version, CancellationToken Token)> _calls = new();

        public IReadOnlyList<(string Cipher, int Version, CancellationToken Token)> Calls => _calls.ToArray();

        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            throw new NotSupportedException("These tests never encrypt.");

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct)
        {
            _calls.Enqueue((encryptedBase64, keyVersion, ct));
            return Task.FromResult(PlainPrefix + encryptedBase64);
        }
    }
}
