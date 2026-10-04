using System.Text.Json;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The overview's "registrations needing attention" list reads CentralDb with a projection of the columns it shows:
/// password hashes, owner contact data, addresses and payment references are never selected.
/// </summary>
public sealed class CentralAdminAttentionListTests : IDisposable
{
    private static readonly string[] UnusedSensitiveColumns =
    [
        "PasswordHash", "OwnerEmail", "OwnerPhone", "OwnerFullName", "BusinessPhone", "BusinessEmail",
        "StreetAddress", "BuildingNumber", "DoorNumber", "Floor", "AddressNote", "PostalCode", "LocationUrl",
        "SimulatedPaymentReference", "DatabaseName"
    ];

    private readonly CentralTestDatabase _central = new();

    public void Dispose() => _central.Dispose();

    [Fact]
    public async Task AttentionList_SelectsOnlyDisplayedColumns_InOneCentralQuery()
    {
        _central.AddRegistration("waiting", PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: DateTime.UtcNow.AddHours(-3));
        _central.Counter.Reset();

        var items = await Service().GetAttentionListAsync(TestContext.Current.CancellationToken);

        var sql = Assert.Single(_central.Counter.Commands);
        foreach (var column in UnusedSensitiveColumns)
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);
        var item = Assert.Single(items);
        Assert.Equal("waiting", item.Slug);
        Assert.Equal(string.Empty, item.OwnerEmail);
        Assert.Equal(string.Empty, item.OwnerFullName);
        Assert.DoesNotContain(SecretMarkers.RegistrationPasswordHash, JsonSerializer.Serialize(items), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttentionList_KeepsItsFilterOrderAndLimit()
    {
        var at = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _central.AddRegistration("paid-late", PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: at.AddHours(5));
        _central.AddRegistration("paid-early", PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: at);
        // No payment time: ordered by creation time instead (09:00 on 1 Jan, earlier than both payments).
        _central.AddRegistration("paid-unknown-time", PendingRegistrationStatus.PaymentSucceeded);
        _central.AddRegistration("awaiting", PendingRegistrationStatus.AwaitingPayment);
        var tenant = _central.AddTenant("already-linked");
        _central.AddRegistration("linked", PendingRegistrationStatus.PaymentSucceeded, tenant.Id, paymentSucceededAt: at.AddHours(-1));
        for (var i = 0; i < 25; i++)
            _central.AddRegistration($"bulk-{i:00}", PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: at.AddDays(1).AddMinutes(i));

        var items = await Service().GetAttentionListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(20, items.Count);
        Assert.Equal(["paid-unknown-time", "paid-early", "paid-late", "bulk-00"], items.Take(4).Select(i => i.Slug));
        Assert.DoesNotContain(items, i => i.Slug is "awaiting" or "linked");
        Assert.All(items, i =>
        {
            Assert.Equal(PendingRegistrationStatus.PaymentSucceeded, i.Status);
            Assert.True(i.IsEligibleForProvisioning);
            Assert.Null(i.TenantId);
        });
    }

    private CentralAdminPendingRegistrationService Service() => new(_central.CreateContext(counted: true));
}
