using System.Globalization;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Plans;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.Web.Controllers;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// Real signup/checkout controllers and <see cref="PendingRegistrationService"/> over an isolated
/// in-memory SQLite CentralDb, so tests can assert the stored registration state. Each controller is a
/// separate "browser": it carries an ownership cookie only when a proof is passed in.
/// </summary>
internal sealed class CheckoutTestHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IOptions<CustomerOnboardingOptions> _options = Options.Create(new CustomerOnboardingOptions());
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly List<CentralDbContext> _extraContexts = [];

    public CheckoutTestHarness()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite(_connection)
            .Options);
        Db.Database.EnsureCreated();
    }

    public CentralDbContext Db { get; }

    public void Dispose()
    {
        foreach (var db in _extraContexts)
            db.Dispose();
        Db.Dispose();
        _connection.Dispose();
    }

    public PendingRegistrationService CreateService() => new(
        Db,
        new WaslaPlanCatalog(),
        new SignupReferenceDataService(Db),
        _options,
        NullLogger<PendingRegistrationService>.Instance);

    /// <summary>
    /// A service over its own CentralDb context on the same database, running <paramref name="interceptor"/>
    /// so a test can make a concurrent change between the service's read and its write.
    /// </summary>
    public PendingRegistrationService CreateService(IInterceptor interceptor)
    {
        var db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options);
        _extraContexts.Add(db);
        return new PendingRegistrationService(
            db,
            new WaslaPlanCatalog(),
            new SignupReferenceDataService(db),
            _options,
            NullLogger<PendingRegistrationService>.Instance);
    }

    /// <summary>A valid proof minted with the same Data Protection keys the controllers use.</summary>
    public string ProofFor(Guid registrationId, DateTimeOffset? expiresAt = null) =>
        new SignupRegistrationOwnership(_dataProtection, new TestWebHostEnvironment(Environments.Production))
            .CreateProof(registrationId, expiresAt ?? DateTimeOffset.UtcNow.AddDays(7));

    public CheckoutController CreateController(string environmentName, string? proof = null) => new(
        CreateService(),
        new WaslaPlanCatalog(),
        new KeyEchoLocalizer(),
        new TestWebHostEnvironment(environmentName),
        _options,
        _dataProtection)
    {
        ControllerContext = new ControllerContext { HttpContext = Browser(proof) }
    };

    public CheckoutController CreateOwnerController(string environmentName, Guid registrationId) =>
        CreateController(environmentName, ProofFor(registrationId));

    public SignupController CreateSignupController(string environmentName, string? proof = null, bool https = false)
    {
        var context = Browser(proof);
        context.Request.Scheme = https ? "https" : "http";
        context.Request.IsHttps = https;

        return new SignupController(
            CreateService(),
            new SignupReferenceDataService(Db),
            new WaslaPlanCatalog(),
            new InlineValidator<PendingRegistrationRequest>(),
            _options,
            new KeyEchoLocalizer(),
            new TestWebHostEnvironment(environmentName),
            _dataProtection)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    public async Task<PendingRegistration> SeedAsync(
        PendingRegistrationStatus status = PendingRegistrationStatus.AwaitingPayment,
        string slug = "simulator")
    {
        var now = DateTime.UtcNow;
        var registration = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            Status = status,
            Slug = slug,
            BusinessName = "Checkout Test Restaurant",
            PrimaryDomain = $"{slug}.wasla.local",
            DatabaseName = $"Wasla_{slug.Replace('-', '_')}",
            BusinessPhone = "5551112233",
            BusinessEmail = "business@example.test",
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            StreetAddress = "Private Street 7",
            OwnerFullName = "Private Owner",
            OwnerEmail = "owner@example.test",
            OwnerPhone = "5329998877",
            PasswordHash = "hash",
            PlanCode = WaslaPlanCodes.Starter,
            BillingPeriod = "Monthly",
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7),
            PaymentSucceededAtUtc = status is PendingRegistrationStatus.PaymentSucceeded
                or PendingRegistrationStatus.Provisioned ? now : null,
            PaymentFailedAtUtc = status == PendingRegistrationStatus.PaymentFailed ? now : null
        };
        Db.PendingRegistrations.Add(registration);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();
        return registration;
    }

    public async Task<PendingRegistration> ReloadAsync(Guid id)
    {
        Db.ChangeTracker.Clear();
        return await Db.PendingRegistrations
            .AsNoTracking()
            .SingleAsync(r => r.Id == id, TestContext.Current.CancellationToken);
    }

    public async Task AssertUnchangedAsync(PendingRegistration seeded)
    {
        var stored = await ReloadAsync(seeded.Id);
        Assert.Equal(seeded.Status, stored.Status);
        Assert.Equal(seeded.PaymentSucceededAtUtc, stored.PaymentSucceededAtUtc);
        Assert.Equal(seeded.PaymentFailedAtUtc, stored.PaymentFailedAtUtc);
        Assert.Equal(seeded.ExpiresAtUtc, stored.ExpiresAtUtc);
        Assert.Null(stored.SimulatedPaymentReference);
        Assert.Null(stored.TenantId);
    }

    public Task<bool> IsSlugAvailableAsync(string slug) =>
        CreateService().IsSlugAvailableAsync(slug, TestContext.Current.CancellationToken);

    private static DefaultHttpContext Browser(string? proof)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.IsHttps = true;
        context.Request.Host = new HostString("wasla.local");
        if (proof is not null)
            context.Request.Headers.Cookie = $"{SignupRegistrationOwnership.CookieName}={proof}";
        return context;
    }

    private sealed class KeyEchoLocalizer : IStringLocalizer<Wasla.Web.SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string ApplicationName { get; set; } = "Wasla.UnitTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
