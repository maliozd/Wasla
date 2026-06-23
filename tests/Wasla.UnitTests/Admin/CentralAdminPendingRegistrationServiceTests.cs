using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

public sealed class CentralAdminPendingRegistrationServiceTests : IDisposable
{
    private readonly CentralDbContext _db;
    private readonly CentralAdminPendingRegistrationService _service;

    public CentralAdminPendingRegistrationServiceTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite($"Data Source={Guid.NewGuid():N}.db")
            .Options;
        _db = new CentralDbContext(options);
        _db.Database.EnsureCreated();
        _service = new CentralAdminPendingRegistrationService(_db);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task GetCountsAsync_ReturnsZeroWhenEmpty()
    {
        var counts = await _service.GetCountsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, counts.Total);
        Assert.Equal(0, counts.PendingPayment);
        Assert.Equal(0, counts.PaymentReceivedSetupPending);
        Assert.Equal(0, counts.Provisioned);
    }

    [Fact]
    public async Task GetCountsAsync_CountsByStatus()
    {
        SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "r1");
        SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "r2");
        SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "r3");
        SeedRegistration(PendingRegistrationStatus.Provisioned, "r4");
        SeedRegistration(PendingRegistrationStatus.Provisioned, "r5");
        SeedRegistration(PendingRegistrationStatus.Cancelled, "r6");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var counts = await _service.GetCountsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, counts.Total);
        Assert.Equal(2, counts.PendingPayment);
        Assert.Equal(1, counts.PaymentReceivedSetupPending);
        Assert.Equal(2, counts.Provisioned);
    }

    [Fact]
    public async Task GetCountsAsync_PaymentReceived_ExcludesProvisioned()
    {
        SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "r1");
        SeedRegistration(PendingRegistrationStatus.Provisioned, "r2");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var counts = await _service.GetCountsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, counts.PaymentReceivedSetupPending);
        Assert.Equal(1, counts.Provisioned);
    }

    [Fact]
    public async Task GetListAsync_FilterPendingPayment_ReturnsOnlyAwaitingPayment()
    {
        SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "r1");
        SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "r2");
        SeedRegistration(PendingRegistrationStatus.Provisioned, "r3");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetListAsync(PendingRegistrationAdminFilter.PendingPayment, TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("r1", result.Items[0].Slug);
    }

    [Fact]
    public async Task GetListAsync_FilterAll_ReturnsAll()
    {
        SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "r1");
        SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "r2");
        SeedRegistration(PendingRegistrationStatus.Provisioned, "r3");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetListAsync(PendingRegistrationAdminFilter.All, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public async Task GetListAsync_OrderedByCreatedAtUtcDescending()
    {
        var now = DateTime.UtcNow;
        SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "r1", now.AddDays(-2));
        SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "r2", now.AddDays(-1));
        SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "r3", now);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetListAsync(PendingRegistrationAdminFilter.All, TestContext.Current.CancellationToken);

        Assert.Equal("r3", result.Items[0].Slug);
        Assert.Equal("r2", result.Items[1].Slug);
        Assert.Equal("r1", result.Items[2].Slug);
    }

    [Fact]
    public async Task GetListAsync_EmptyResults_HandledGracefully()
    {
        var result = await _service.GetListAsync(PendingRegistrationAdminFilter.Provisioned, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Items);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetAttentionListAsync_ReturnsOnlyPaymentSucceededRegistrations()
    {
        SeedRegistration(PendingRegistrationStatus.AwaitingPayment, "r1");
        SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "r2");
        SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "r3");
        SeedRegistration(PendingRegistrationStatus.Provisioned, "r4");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var attention = await _service.GetAttentionListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, attention.Count);
        Assert.All(attention, r => Assert.True(r.IsEligibleForProvisioning));
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNullForMissingId()
    {
        var result = await _service.GetDetailAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsCorrectDetail()
    {
        var reg = SeedRegistration(PendingRegistrationStatus.PaymentSucceeded, "testslug");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var detail = await _service.GetDetailAsync(reg.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal(reg.Id, detail.Id);
        Assert.Equal("testslug", detail.Slug);
        Assert.True(detail.IsEligibleForProvisioning);
    }

    [Fact]
    public async Task GetDetailAsync_ProvisionedRegistration_IsNotEligible()
    {
        var reg = SeedRegistration(PendingRegistrationStatus.Provisioned, "testslug");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var detail = await _service.GetDetailAsync(reg.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.False(detail.IsEligibleForProvisioning);
    }

    private PendingRegistration SeedRegistration(
        PendingRegistrationStatus status,
        string slug,
        DateTime? createdAt = null)
    {
        var reg = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            Status = status,
            Slug = slug,
            BusinessName = $"Business {slug}",
            PrimaryDomain = $"{slug}.wasla.local",
            DatabaseName = $"Wasla_{slug}",
            BusinessPhone = "+905551112233",
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            OwnerFullName = "Test Owner",
            OwnerEmail = $"{slug}@test.com",
            PasswordHash = "hash",
            PlanCode = "starter",
            BillingPeriod = "monthly",
            CreatedAtUtc = createdAt ?? DateTime.UtcNow
        };
        _db.PendingRegistrations.Add(reg);
        return reg;
    }
}
