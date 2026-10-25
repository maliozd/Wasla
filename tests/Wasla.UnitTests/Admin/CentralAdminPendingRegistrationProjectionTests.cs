using System.Text.Json;
using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The Pending Registrations list and detail read CentralDb with one server-side projection each (WAS-38): the
/// registration's password hash and payment reference, contact and address columns the page does not show, and the
/// linked tenant's connection string are never selected.
/// </summary>
public sealed class CentralAdminPendingRegistrationProjectionTests : IDisposable
{
    private static readonly string[] NeverSelected =
    [
        "PasswordHash", "SimulatedPaymentReference", "EncryptedConnectionString", "EncryptionKeyVersion",
        "StreetAddress", "BuildingNumber", "DoorNumber", "Floor", "AddressNote", "PostalCode", "LocationUrl"
    ];

    /// <summary>Columns the detail page shows but the list does not.</summary>
    private static readonly string[] DetailOnly =
    [
        "OwnerEmail", "OwnerPhone", "BusinessPhone", "BusinessEmail", "PrimaryDomain", "DatabaseName",
        "Country", "City", "District", "Neighborhood"
    ];

    private readonly CentralTestDatabase _central = new();

    public void Dispose() => _central.Dispose();

    [Fact]
    public async Task List_SelectsOnlyListedColumns_InOneCentralQuery()
    {
        var tenant = _central.AddTenant("linked");
        _central.AddRegistration("linked", PendingRegistrationStatus.Provisioned, tenant.Id);
        _central.AddRegistration("waiting", PendingRegistrationStatus.PaymentSucceeded);
        _central.Counter.Reset();

        var result = await Service().GetListAsync(PendingRegistrationAdminFilter.All, TestContext.Current.CancellationToken);

        foreach (var column in NeverSelected.Concat(DetailOnly))
            Assert.All(_central.Counter.Commands, sql => Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal));
        // The list shows no tenant domain, so the Tenants table is not read at all.
        var sql = Assert.Single(_central.Counter.Commands);
        Assert.DoesNotContain("\"Tenants\"", sql, StringComparison.Ordinal);

        Assert.Equal(2, result.Items.Count);
        var linked = Assert.Single(result.Items, i => i.Slug == "linked");
        Assert.Equal("Registration linked", linked.BusinessName);
        Assert.Equal("Owner Person", linked.OwnerFullName);
        Assert.Equal(tenant.Id, linked.TenantId);
        AssertNoSecret(JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task List_KeepsFiltersOrderAndEligibility()
    {
        var at = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var tenant = _central.AddTenant("provisioned");
        _central.AddRegistration("awaiting", PendingRegistrationStatus.AwaitingPayment, createdAt: at.AddHours(1));
        _central.AddRegistration("paid", PendingRegistrationStatus.PaymentSucceeded, createdAt: at.AddHours(3));
        // Paid but already linked to a tenant: listed under "payment received", but not eligible.
        _central.AddRegistration("paid-linked", PendingRegistrationStatus.PaymentSucceeded, tenant.Id, createdAt: at.AddHours(2));
        _central.AddRegistration("provisioned", PendingRegistrationStatus.Provisioned, tenant.Id, createdAt: at);
        _central.AddRegistration("cancelled", PendingRegistrationStatus.Cancelled, createdAt: at.AddHours(4));
        var tieA = _central.AddRegistration("tie-a", PendingRegistrationStatus.AwaitingPayment, createdAt: at.AddHours(-1));
        var tieB = _central.AddRegistration("tie-b", PendingRegistrationStatus.AwaitingPayment, createdAt: at.AddHours(-1));
        var ct = TestContext.Current.CancellationToken;

        var all = await Service().GetListAsync(PendingRegistrationAdminFilter.All, ct);
        var pendingPayment = await Service().GetListAsync(PendingRegistrationAdminFilter.PendingPayment, ct);
        var paymentReceived = await Service().GetListAsync(PendingRegistrationAdminFilter.PaymentReceivedSetupPending, ct);
        var provisioned = await Service().GetListAsync(PendingRegistrationAdminFilter.Provisioned, ct);

        // Newest first; equal creation times keep their id order.
        var tie = tieA.Id.CompareTo(tieB.Id) < 0 ? new[] { "tie-a", "tie-b" } : ["tie-b", "tie-a"];
        Assert.Equal(["cancelled", "paid", "paid-linked", "awaiting", "provisioned", .. tie], all.Items.Select(i => i.Slug));
        Assert.Equal(["awaiting", .. tie], pendingPayment.Items.Select(i => i.Slug));
        Assert.Equal(["paid", "paid-linked"], paymentReceived.Items.Select(i => i.Slug));
        Assert.Equal(["provisioned"], provisioned.Items.Select(i => i.Slug));
        Assert.Equal(PendingRegistrationAdminFilter.PaymentReceivedSetupPending, paymentReceived.AppliedFilter);

        Assert.Equal(["paid"], all.Items.Where(i => i.IsEligibleForProvisioning).Select(i => i.Slug));
        Assert.Equal(PendingRegistrationStatus.Provisioned, provisioned.Items[0].Status);
        Assert.NotNull(provisioned.Items[0].ProvisionedAtUtc);
    }

    [Fact]
    public async Task Detail_SelectsOnlyShownColumns_AndTheTenantDomain_InOneCentralQuery()
    {
        var tenant = _central.AddTenant("linked", encryptedConnectionString: SecretMarkers.EncryptedConnectionString);
        var registration = _central.AddRegistration("linked", PendingRegistrationStatus.Provisioned, tenant.Id);
        _central.Counter.Reset();

        var detail = await Service().GetDetailAsync(registration.Id, TestContext.Current.CancellationToken);

        foreach (var column in NeverSelected)
            Assert.All(_central.Counter.Commands, sql => Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal));
        Assert.Single(_central.Counter.Commands);

        Assert.NotNull(detail);
        Assert.Equal("linked.app.wasla.local", detail.TenantDomain);
        Assert.Equal(tenant.Id, detail.TenantId);
        Assert.Equal("Wasla_Tenant_linked", detail.DatabaseName);
        Assert.Equal("linked@example.test", detail.OwnerEmail);
        Assert.Equal("5551112233", detail.BusinessPhone);
        Assert.Equal("Istanbul", detail.City);
        Assert.False(detail.IsEligibleForProvisioning);
        AssertNoSecret(JsonSerializer.Serialize(detail));
    }

    [Fact]
    public async Task Detail_OfAPaidRegistrationWithoutTenant_IsEligible_AndHasNoTenantDomain()
    {
        var paid = _central.AddRegistration("paid", PendingRegistrationStatus.PaymentSucceeded);
        // A registration pointing at a tenant row that no longer exists shows no domain and is not eligible.
        var dangling = _central.AddRegistration("dangling", PendingRegistrationStatus.PaymentSucceeded, Guid.NewGuid());
        var ct = TestContext.Current.CancellationToken;
        _central.Counter.Reset();

        var paidDetail = await Service().GetDetailAsync(paid.Id, ct);
        var danglingDetail = await Service().GetDetailAsync(dangling.Id, ct);
        var missing = await Service().GetDetailAsync(Guid.NewGuid(), ct);

        Assert.Equal(3, _central.Counter.Count);
        Assert.NotNull(paidDetail);
        Assert.True(paidDetail.IsEligibleForProvisioning);
        Assert.Null(paidDetail.TenantDomain);
        Assert.NotNull(danglingDetail);
        Assert.False(danglingDetail.IsEligibleForProvisioning);
        Assert.Null(danglingDetail.TenantDomain);
        Assert.Null(missing);
    }

    private static void AssertNoSecret(string output)
    {
        foreach (var secret in SecretMarkers.All)
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
    }

    private CentralAdminPendingRegistrationService Service() => new(_central.CreateContext(counted: true));
}
