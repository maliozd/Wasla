using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Central.Migrations;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// The CentralDb mapping of the billing tables and the migration that creates them (WAS-147): owners, unique rules,
/// check constraints, the concurrency token, immutable snapshot columns, and a migration that only adds.
/// </summary>
public sealed class BillingPersistenceModelTests
{
    private static CentralDbContext SqlServerModel() => new(new DbContextOptionsBuilder<CentralDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);

    [Fact]
    public void TheCentralModel_HasNoChangesWithoutAMigration_AndContainsTheBillingMigration()
    {
        using var central = SqlServerModel();

        Assert.False(central.Database.HasPendingModelChanges());
        Assert.Contains(central.Database.GetMigrations(), id => id.EndsWith("_AddBillingSubscriptions", StringComparison.Ordinal));
    }

    [Fact]
    public void Subscriptions_AreOwnedByTheirRegistration_AndOptionallyLinkedToOneTenant()
    {
        using var central = SqlServerModel();
        var entity = central.Model.FindEntityType(typeof(BillingSubscription))!;
        Assert.Equal("BillingSubscriptions", entity.GetTableName());

        var registration = Assert.Single(entity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(PendingRegistration));
        Assert.True(registration.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, registration.DeleteBehavior);
        var tenant = Assert.Single(entity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(Tenant));
        Assert.False(tenant.IsRequired);
        Assert.Equal(DeleteBehavior.SetNull, tenant.DeleteBehavior);
        var planPrice = Assert.Single(entity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(BillingPlanPrice));
        Assert.True(planPrice.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, planPrice.DeleteBehavior);
        Assert.Equal(nameof(BillingSubscription.PlanPriceId), Assert.Single(planPrice.Properties).Name);

        AssertUniqueIndex(entity, "IX_BillingSubscriptions_PendingRegistrationId", null, nameof(BillingSubscription.PendingRegistrationId));
        AssertUniqueIndex(entity, "IX_BillingSubscriptions_TenantId", "[TenantId] IS NOT NULL", nameof(BillingSubscription.TenantId));
        AssertUniqueIndex(
            entity,
            "IX_BillingSubscriptions_ProviderReference",
            "[ExternalSubscriptionReference] IS NOT NULL",
            nameof(BillingSubscription.Provider),
            nameof(BillingSubscription.Environment),
            nameof(BillingSubscription.ExternalSubscriptionReference));

        Assert.True(entity.FindProperty(nameof(BillingSubscription.Revision))!.IsConcurrencyToken);
        Assert.Null(entity.FindProperty(nameof(BillingSubscription.Price)));
    }

    [Fact]
    public void SubscriptionSnapshotColumns_AndEveryPaymentColumn_ThrowWhenChangedAfterSave()
    {
        using var central = SqlServerModel();
        var subscription = central.Model.FindEntityType(typeof(BillingSubscription))!;
        foreach (var name in new[]
                 {
                     nameof(BillingSubscription.PendingRegistrationId), nameof(BillingSubscription.PlanPriceId),
                     nameof(BillingSubscription.Provider),
                     nameof(BillingSubscription.Environment), nameof(BillingSubscription.PlanCode),
                     nameof(BillingSubscription.BillingPeriod), nameof(BillingSubscription.Amount),
                     nameof(BillingSubscription.Currency), nameof(BillingSubscription.CreatedAtUtc)
                 })
            Assert.Equal(PropertySaveBehavior.Throw, subscription.FindProperty(name)!.GetAfterSaveBehavior());

        foreach (var name in new[] { nameof(BillingSubscription.Status), nameof(BillingSubscription.TenantId), nameof(BillingSubscription.Revision) })
            Assert.Equal(PropertySaveBehavior.Save, subscription.FindProperty(name)!.GetAfterSaveBehavior());

        var payment = central.Model.FindEntityType(typeof(BillingPayment))!;
        Assert.All(payment.GetProperties(), p => Assert.Equal(PropertySaveBehavior.Throw, p.GetAfterSaveBehavior()));
        var planPrice = central.Model.FindEntityType(typeof(BillingPlanPrice))!;
        Assert.All(planPrice.GetProperties(), p => Assert.Equal(PropertySaveBehavior.Throw, p.GetAfterSaveBehavior()));
    }

    [Fact]
    public void PlanPrices_AreUniquePerPlanPeriodAndEffectiveTime_WithPositiveAmounts()
    {
        using var central = SqlServerModel();
        var model = central.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(BillingPlanPrice))!;
        Assert.Equal("BillingPlanPrices", entity.GetTableName());

        AssertUniqueIndex(
            entity,
            "IX_BillingPlanPrices_PlanCode_BillingPeriod_EffectiveFromUtc",
            null,
            nameof(BillingPlanPrice.PlanCode),
            nameof(BillingPlanPrice.BillingPeriod),
            nameof(BillingPlanPrice.EffectiveFromUtc));
        var checks = entity.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);
        Assert.Equal("[Amount] > 0", checks["CK_BillingPlanPrices_Amount"]);
        Assert.Equal("[BillingPeriod] IN ('Monthly', 'Yearly')", checks["CK_BillingPlanPrices_BillingPeriod"]);
        Assert.Empty(entity.GetForeignKeys());
        Assert.Null(entity.FindProperty(nameof(BillingPlanPrice.Price)));
        Assert.False(entity.FindProperty(nameof(BillingPlanPrice.VatIncluded))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(BillingPlanPrice.CreatedByCentralAdminUserId))!.IsNullable);
    }

    [Fact]
    public void Payments_AreUniquePerProviderEnvironmentAndPeriod_AndShareTheirSubscriptionsEnvironment()
    {
        using var central = SqlServerModel();
        var entity = central.Model.FindEntityType(typeof(BillingPayment))!;
        Assert.Equal("BillingPayments", entity.GetTableName());

        AssertUniqueIndex(
            entity,
            "IX_BillingPayments_ProviderReference",
            null,
            nameof(BillingPayment.Provider),
            nameof(BillingPayment.Environment),
            nameof(BillingPayment.ExternalPaymentReference));
        AssertUniqueIndex(entity, "IX_BillingPayments_SubscriptionId_PeriodStartUtc", null, nameof(BillingPayment.SubscriptionId), nameof(BillingPayment.PeriodStartUtc));

        var owner = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(BillingSubscription), owner.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, owner.DeleteBehavior);
        Assert.Equal(
            [nameof(BillingPayment.SubscriptionId), nameof(BillingPayment.Provider), nameof(BillingPayment.Environment)],
            owner.Properties.Select(p => p.Name));
        Assert.Equal(
            [nameof(BillingSubscription.Id), nameof(BillingSubscription.Provider), nameof(BillingSubscription.Environment)],
            owner.PrincipalKey.Properties.Select(p => p.Name));
    }

    [Fact]
    public void CheckConstraints_AllowOnlyBillingStates_AndRequireAProviderReferenceForVerifiedStates()
    {
        using var central = SqlServerModel();
        var model = central.GetService<IDesignTimeModel>().Model;
        var subscription = model.FindEntityType(typeof(BillingSubscription))!;
        var checks = subscription.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);

        var allowed = SubscriptionStatusTransitions.BillingStates.Select(s => ((int)s).ToString()).Order();
        var inList = checks["CK_BillingSubscriptions_Status"]["[Status] IN (".Length..^1].Split(", ").Order();
        Assert.Equal(allowed, inList);
        Assert.Equal("[Status] NOT IN (2, 3) OR [ExternalSubscriptionReference] IS NOT NULL", checks["CK_BillingSubscriptions_VerifiedStateHasProviderReference"]);
        Assert.Equal("[Amount] > 0", checks["CK_BillingSubscriptions_Amount"]);
        Assert.Equal("[Environment] IN (1, 2)", checks["CK_BillingSubscriptions_Environment"]);
        Assert.Equal("[Provider] IN (1)", checks["CK_BillingSubscriptions_Provider"]);

        var payment = model.FindEntityType(typeof(BillingPayment))!;
        Assert.Contains(payment.GetCheckConstraints(), c => c.Name == "CK_BillingPayments_Period" && c.Sql == "[PeriodEndUtc] > [PeriodStartUtc]");
    }

    [Fact]
    public void TheMigration_OnlyAddsTheBillingTables_AndItsRollbackRefusesToDeleteBillingRows()
    {
        var migration = new AddBillingSubscriptions();

        var touched = migration.UpOperations.Select(op => op switch
        {
            CreateTableOperation t => t.Name,
            CreateIndexOperation i => i.Table,
            _ => throw new Xunit.Sdk.XunitException($"Unexpected operation {op.GetType().Name}: the migration must only add.")
        }).Distinct().Order();
        Assert.Equal(["BillingPayments", "BillingPlanPrices", "BillingSubscriptions"], touched);

        // No plan price rows: the amounts are a business decision not taken yet (see docs/architecture/billing.md).
        Assert.DoesNotContain(migration.UpOperations, op => op is InsertDataOperation or SqlOperation);

        var guard = Assert.IsType<SqlOperation>(migration.DownOperations[0]);
        Assert.Contains("THROW 50147", guard.Sql, StringComparison.Ordinal);
        Assert.Contains("[BillingPlanPrices]", guard.Sql, StringComparison.Ordinal);
        Assert.Equal(
            ["BillingPayments", "BillingSubscriptions", "BillingPlanPrices"],
            migration.DownOperations.Skip(1).Select(op => Assert.IsType<DropTableOperation>(op).Name));
    }

    private static void AssertUniqueIndex(IEntityType entity, string name, string? filter, params string[] columns)
    {
        var index = Assert.Single(entity.GetIndexes(), i => i.GetDatabaseName() == name);
        Assert.True(index.IsUnique, name);
        Assert.Equal(filter, index.GetFilter());
        Assert.Equal(columns, index.Properties.Select(p => p.Name));
    }
}
