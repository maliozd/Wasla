using Wasla.Application.Onboarding;
using Wasla.Cli;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// WAS-93: tenant database names are server-assigned and never CentralDb or a system database; an existing database is
/// only reused with this registration's ownership marker; CLI drops and resets refuse anything ambiguous.
/// </summary>
public sealed class TenantDatabaseSafetyTests
{
    private const string CentralConnection = "Server=(localdb)\\Fixture;Database=Wasla_Central;Trusted_Connection=True";

    // --- Names --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("central", "Central")]
    [InlineData("sushi-place", "SushiPlace")]
    [InlineData("2kardes", "T2kardes")]
    [InlineData("---", "Tenant")]
    public void Generate_UsesTheSlugOnlyAsALabel_AndAddsARandomSuffix(string slug, string label)
    {
        var name = TenantDatabaseNames.Generate(slug);

        Assert.Matches($"^Wasla_{label}_[0-9a-f]{{12}}$", name);
        Assert.True(TenantDatabaseNames.IsGeneratedFormat(name));
        Assert.Null(TenantDatabaseNames.GetRejectionReason(name, "Wasla_Central"));
    }

    [Fact]
    public void Generate_NeverRepeatsForTheSameSlug()
    {
        var names = Enumerable.Range(0, 200).Select(_ => TenantDatabaseNames.Generate("central")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(200, names.Count);
        Assert.DoesNotContain("Wasla_Central", names);
    }

    [Fact]
    public void Generate_KeepsLongSlugsWithinTheSqlServerLimit()
    {
        var name = TenantDatabaseNames.Generate(new string('a', 100));

        Assert.True(name.Length <= TenantDatabaseNames.MaxLength);
    }

    [Theory]
    [InlineData("Wasla_Central")]
    [InlineData("wasla_central")]
    [InlineData(" WASLA_CENTRAL ")]
    [InlineData("master")]
    [InlineData("MASTER")]
    [InlineData("tempdb")]
    [InlineData("model")]
    [InlineData("msdb")]
    [InlineData("mssqlsystemresource")]
    [InlineData("distribution")]
    [InlineData("Wasla;DROP")]
    [InlineData("Wasla]x")]
    [InlineData("")]
    public void GetRejectionReason_RefusesCentralDbSystemAndMalformedNames(string name)
    {
        Assert.NotNull(TenantDatabaseNames.GetRejectionReason(name, "Wasla_Central"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GetRejectionReason_FailsClosed_WhenTheCentralDbCatalogIsUnknown(string? centralCatalog)
    {
        Assert.NotNull(TenantDatabaseNames.GetRejectionReason("Wasla_SushiPlace_3f9a1c2b7d4e", centralCatalog));
    }

    // --- Ownership marker ---------------------------------------------------------------------------

    [Fact]
    public void Evaluate_AMissingDatabase_IsCreated()
    {
        var marker = TenantDatabaseOwnership.ForRegistration(Guid.NewGuid());

        Assert.Equal(TenantDatabaseClaim.Create, TenantDatabaseOwnership.Evaluate("Wasla_x", exists: false, marker: null, marker));
    }

    [Fact]
    public void Evaluate_AnExistingDatabaseWithThisRegistrationsMarker_IsResumed()
    {
        var marker = TenantDatabaseOwnership.ForRegistration(Guid.NewGuid());

        Assert.Equal(TenantDatabaseClaim.Resume, TenantDatabaseOwnership.Evaluate("Wasla_x", exists: true, marker, marker));
    }

    [Fact]
    public void Evaluate_AnExistingDatabaseWithoutAMarker_IsNeverAdopted()
    {
        var marker = TenantDatabaseOwnership.ForRegistration(Guid.NewGuid());

        var ex = Assert.Throws<TenantDatabaseOwnershipException>(
            () => TenantDatabaseOwnership.Evaluate("Wasla_Central", exists: true, marker: null, marker));
        Assert.Equal("Wasla_Central", ex.DatabaseName);
    }

    [Fact]
    public void Evaluate_AnExistingDatabaseMarkedForAnotherRegistrationOrTenant_IsNeverAdopted()
    {
        var registrationId = Guid.NewGuid();
        var expected = TenantDatabaseOwnership.ForRegistration(registrationId);

        Assert.Throws<TenantDatabaseOwnershipException>(() => TenantDatabaseOwnership.Evaluate(
            "Wasla_x", exists: true, TenantDatabaseOwnership.ForRegistration(Guid.NewGuid()), expected));
        // The same GUID as a tenant marker is a different owner.
        Assert.Throws<TenantDatabaseOwnershipException>(() => TenantDatabaseOwnership.Evaluate(
            "Wasla_x", exists: true, TenantDatabaseOwnership.ForTenant(registrationId), expected));
        // Markers compare exactly.
        Assert.Throws<TenantDatabaseOwnershipException>(() => TenantDatabaseOwnership.Evaluate(
            "Wasla_x", exists: true, expected.ToUpperInvariant(), expected));
    }

    // --- CLI destructive guards ---------------------------------------------------------------------

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "  ", true)]
    [InlineData("Production", null, true)]
    [InlineData(null, "production", true)]
    [InlineData("Development", "Production", false)]
    [InlineData("Development", null, false)]
    [InlineData(null, "Staging", false)]
    public void IsProductionEnvironment_CountsAnUnsetEnvironmentAsProduction(string? dotnet, string? aspnet, bool expected)
    {
        Assert.Equal(expected, CliDestructiveGuards.IsProductionEnvironment(dotnet, aspnet));
    }

    [Fact]
    public void GetDatabaseRefusalReason_AllowsASingleTenantsOwnDatabase()
    {
        Assert.Null(CliDestructiveGuards.GetDatabaseRefusalReason(
            "Wasla_SushiPlace", TenantConnection("Wasla_SushiPlace"), CentralConnection, tenantsReferencingDatabase: 1));
    }

    [Theory]
    [InlineData("Wasla_Central")]
    [InlineData("wasla_central")]
    public void GetDatabaseRefusalReason_RefusesCentralDb(string databaseName)
    {
        var reason = CliDestructiveGuards.GetDatabaseRefusalReason(
            databaseName, TenantConnection(databaseName), CentralConnection, tenantsReferencingDatabase: 1);

        Assert.NotNull(reason);
        Assert.Contains("CentralDb", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDatabaseRefusalReason_RefusesASystemDatabase()
    {
        Assert.NotNull(CliDestructiveGuards.GetDatabaseRefusalReason(
            "master", TenantConnection("master"), CentralConnection, tenantsReferencingDatabase: 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void GetDatabaseRefusalReason_RefusesADatabaseNotOwnedByExactlyOneTenant(int references)
    {
        Assert.NotNull(CliDestructiveGuards.GetDatabaseRefusalReason(
            "Wasla_SushiPlace", TenantConnection("Wasla_SushiPlace"), CentralConnection, references));
    }

    [Fact]
    public void GetDatabaseRefusalReason_RefusesAConnectionStringTargetingAnotherDatabase()
    {
        // The registered name looks harmless, but the stored connection string points at CentralDb.
        Assert.NotNull(CliDestructiveGuards.GetDatabaseRefusalReason(
            "Wasla_SushiPlace", TenantConnection("Wasla_Central"), CentralConnection, tenantsReferencingDatabase: 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Server=(localdb)\\Fixture;Trusted_Connection=True")]
    [InlineData("not a connection string")]
    public void GetDatabaseRefusalReason_FailsClosed_WhenCentralDbIsNotConfigured(string? centralConnection)
    {
        Assert.NotNull(CliDestructiveGuards.GetDatabaseRefusalReason(
            "Wasla_SushiPlace", TenantConnection("Wasla_SushiPlace"), centralConnection, tenantsReferencingDatabase: 1));
    }

    [Fact]
    public void GetDatabaseRefusalReason_DoesNotRepeatConnectionStringSecrets()
    {
        var tenant = "Server=(localdb)\\Fixture;Database=Wasla_Central;User ID=fake-user;Password=fake-pass-value";

        var reason = CliDestructiveGuards.GetDatabaseRefusalReason("Wasla_SushiPlace", tenant, CentralConnection, 1);

        Assert.NotNull(reason);
        Assert.DoesNotContain("fake-pass-value", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-user", reason, StringComparison.Ordinal);
    }

    private static string TenantConnection(string database) =>
        $"Server=(localdb)\\Fixture;Database={database};Trusted_Connection=True";
}
