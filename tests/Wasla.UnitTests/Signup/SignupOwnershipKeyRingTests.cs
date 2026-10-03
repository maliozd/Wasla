using System.Net;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The signup ownership cookie is a Data Protection payload, so it survives a restart, a second
/// instance, or key rotation only when every instance uses the same application name and a shared,
/// persisted key ring. Program.cs provides both through SetApplicationName("Wasla") and
/// DataProtection:KeyPath; these tests run the real cookie through hosts configured that way.
/// </summary>
public sealed class SignupOwnershipKeyRingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<string> _keyRings = [];

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        foreach (var keyRing in _keyRings)
        {
            try { Directory.Delete(keyRing, recursive: true); } catch (IOException) { }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ProofIssuedBeforeARestart_IsStillAcceptedAfterIt()
    {
        var keyRing = NewKeyRing();
        string proof;
        await using (var beforeRestart = await SignupWebHost.StartAsync(_connection, keyRing))
            proof = await SignUpAndGetProofAsync(beforeRestart);

        await using var afterRestart = await SignupWebHost.StartAsync(_connection, keyRing);

        await AssertOwnerAccessAsync(afterRestart, proof, await IdOfLatestSignupAsync());
    }

    [Fact]
    public async Task ProofIssuedByOneInstance_IsAcceptedByASecondInstanceSharingTheKeyRing()
    {
        var keyRing = NewKeyRing();
        await using var instanceA = await SignupWebHost.StartAsync(_connection, keyRing);
        await using var instanceB = await SignupWebHost.StartAsync(_connection, keyRing);

        var proof = await SignUpAndGetProofAsync(instanceA);

        await AssertOwnerAccessAsync(instanceB, proof, await IdOfLatestSignupAsync());
    }

    [Fact]
    public async Task KeyRotation_KeepsEarlierProofsValid_AndNewProofsWorkOnEveryInstance()
    {
        var keyRing = NewKeyRing();
        await using var instanceA = await SignupWebHost.StartAsync(_connection, keyRing);
        await using var instanceB = await SignupWebHost.StartAsync(_connection, keyRing);
        var proofBeforeRotation = await SignUpAndGetProofAsync(instanceA);
        var firstRegistration = await IdOfLatestSignupAsync();

        var now = DateTimeOffset.UtcNow;
        instanceB.Services.GetRequiredService<IKeyManager>().CreateNewKey(now, now.AddDays(90));
        var proofAfterRotation = await SignUpAndGetProofAsync(instanceB);
        var secondRegistration = await IdOfLatestSignupAsync();

        Assert.Equal(2, Directory.GetFiles(keyRing, "key-*.xml").Length);
        Assert.NotEqual(KeyIdOf(proofBeforeRotation), KeyIdOf(proofAfterRotation));
        await AssertOwnerAccessAsync(instanceA, proofBeforeRotation, firstRegistration);
        await AssertOwnerAccessAsync(instanceB, proofBeforeRotation, firstRegistration);
        await AssertOwnerAccessAsync(instanceA, proofAfterRotation, secondRegistration);
    }

    [Fact]
    public async Task InstancesWithSeparateKeyRings_DoNotAcceptEachOthersProofs()
    {
        await using var instanceA = await SignupWebHost.StartAsync(_connection, NewKeyRing());
        await using var instanceB = await SignupWebHost.StartAsync(_connection, NewKeyRing());

        var proof = await SignUpAndGetProofAsync(instanceA);

        await AssertPublicOnlyAsync(instanceB, proof, await IdOfLatestSignupAsync());
    }

    [Fact]
    public async Task InstancesWithADifferentApplicationName_DoNotAcceptEachOthersProofs()
    {
        var keyRing = NewKeyRing();
        await using var instanceA = await SignupWebHost.StartAsync(_connection, keyRing);
        await using var instanceB = await SignupWebHost.StartAsync(_connection, keyRing, applicationName: "Wasla.Other");

        var proof = await SignUpAndGetProofAsync(instanceA);

        await AssertPublicOnlyAsync(instanceB, proof, await IdOfLatestSignupAsync());
    }

    // Links the hosts above to the real composition root: the same application name and key ring setting.
    [Fact]
    public void WebProgram_UsesTheWaslaApplicationNameAndTheConfiguredKeyRing()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wasla.Web", "Program.cs"));

        Assert.Contains(".SetApplicationName(\"Wasla\")", program);
        Assert.Contains("builder.Configuration[\"DataProtection:KeyPath\"]", program);
        Assert.Contains("PersistKeysToFileSystem(", program);
    }

    private async Task<string> SignUpAndGetProofAsync(SignupWebHost host)
    {
        using var browser = host.NewBrowser();
        var token = await browser.GetAntiforgeryTokenAsync("/signup");
        var slug = $"s{SentinelApplicant.NewToken()}";
        var submitted = await browser.PostFormAsync("/signup",
        [
            new("__RequestVerificationToken", token),
            new("PlanCode", "Starter"),
            new("BillingPeriod", "Monthly"),
            new("BusinessName", "Key Ring Test"),
            new("SelectedBusinessTypeCodes", "burger"),
            new("BusinessPhoneType", "Mobile"),
            new("BusinessPhone", SentinelApplicant.NewPhone()),
            new("Slug", slug),
            new("Country", "Germany"),
            new("CityId", "1"),
            new("DistrictId", "1"),
            new("City", "Berlin"),
            new("District", "Mitte"),
            new("StreetAddress", "Main Street 1"),
            new("OwnerFullName", $"Keyringowner{slug}"),
            new("OwnerEmail", $"{slug}@sentinel.test"),
            new("Password", "Sentinel-pass-1"),
            new("ConfirmPassword", "Sentinel-pass-1")
        ]);

        Assert.Equal(HttpStatusCode.Redirect, submitted.Status);
        var proof = browser.GetCookie(SignupWebHost.CentralHost, SignupRegistrationOwnership.CookieName);
        Assert.False(string.IsNullOrEmpty(proof));
        return proof!;
    }

    private static async Task AssertOwnerAccessAsync(SignupWebHost host, string proof, Guid registrationId)
    {
        using var browser = host.NewBrowser();
        browser.SetCookie(SignupWebHost.CentralHost, SignupRegistrationOwnership.CookieName, proof);

        var review = await browser.GetAsync($"/checkout/review/{registrationId}");

        Assert.Equal(HttpStatusCode.OK, review.Status);
        Assert.Contains("Keyringowner", review.Body);
    }

    private static async Task AssertPublicOnlyAsync(SignupWebHost host, string proof, Guid registrationId)
    {
        using var browser = host.NewBrowser();
        browser.SetCookie(SignupWebHost.CentralHost, SignupRegistrationOwnership.CookieName, proof);

        var review = await browser.GetAsync($"/checkout/review/{registrationId}");
        var pending = await browser.GetAsync($"/signup/pending/{registrationId}");

        Assert.Equal(HttpStatusCode.Redirect, review.Status);
        Assert.Equal($"/signup/pending/{registrationId}", review.Location);
        Assert.Equal(HttpStatusCode.OK, pending.Status);
        Assert.DoesNotContain("Keyringowner", pending.Body);
    }

    /// <summary>The key id is embedded in the payload header (bytes 4–19 after the magic header).</summary>
    private static Guid KeyIdOf(string proof)
    {
        var bytes = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(proof);
        return new Guid(bytes.AsSpan(4, 16));
    }

    private async Task<Guid> IdOfLatestSignupAsync()
    {
        await using var db = NewDb();
        return await db.PendingRegistrations.AsNoTracking()
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => r.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    private string NewKeyRing()
    {
        var path = Directory.CreateTempSubdirectory("wasla-signup-keys-").FullName;
        _keyRings.Add(path);
        return path;
    }

    private CentralDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wasla.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Wasla.sln was not found.");
    }
}
