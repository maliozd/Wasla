using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// The persistent state a <see cref="TenantSessionWebHost"/> runs on: a SQLite CentralDb registering the tenants
/// <c>alpha</c> and <c>beta</c>, one SQLite database file per tenant, and a Data Protection key folder. A host started
/// without one creates its own and deletes it with the host. A test that creates one can start several hosts over it,
/// side by side or one after another, each with its own service provider and its own <see cref="RecordingTenantDbFactory"/>
/// over the same files, as separate Web instances or a restarted Web over shared databases and a shared key ring. It
/// outlives those hosts and deletes its files when disposed. Data is fake.
/// </summary>
internal sealed class TenantSessionStorage : IDisposable
{
    private readonly string _tenantsDirectory;

    public TenantSessionStorage()
    {
        Central = new CentralTestDatabase();
        AlphaId = Central.AddTenant("alpha").Id;
        BetaId = Central.AddTenant("beta").Id;
        _tenantsDirectory = Directory.CreateTempSubdirectory("wasla-tenants-").FullName;
        using var setup = OpenTenants();
        setup.CreateTenantDatabase(AlphaId);
        setup.CreateTenantDatabase(BetaId);
        Keys = Directory.CreateTempSubdirectory("wasla-session-keys-");
    }

    public CentralTestDatabase Central { get; }

    public Guid AlphaId { get; }

    public Guid BetaId { get; }

    /// <summary>A key folder for hosts that share one key ring (pass it as the host's Data Protection key folder).</summary>
    public DirectoryInfo Keys { get; }

    /// <summary>A new factory over the tenant database files. Disposing it leaves the files in place.</summary>
    public RecordingTenantDbFactory OpenTenants() => new(_tenantsDirectory, ownsDirectory: false);

    // Pooling=False everywhere: no open handle keeps the files once every host has stopped.
    public void Dispose()
    {
        Central.Dispose();
        try { Directory.Delete(_tenantsDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Keys.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
