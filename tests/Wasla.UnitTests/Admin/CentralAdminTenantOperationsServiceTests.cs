using System.Text.Json;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

public sealed class CentralAdminTenantOperationsServiceTests : IDisposable
{
    private static readonly string[] Plans = ["Starter", "Pro", "ProPlus", "Enterprise"];
    private static readonly DateTime Now = TenantSeed.Now;

    private readonly CentralTestDatabase _central = new();
    private readonly FixedTime _time = new(Now);

    public void Dispose() => _central.Dispose();

    // Pagination ------------------------------------------------------------------------------------

    [Fact]
    public async Task List_PaginatesOnTheServer_AndClampsAPageBeyondTheEnd()
    {
        for (var i = 0; i < 23; i++)
            _central.AddTenant($"t{i:00}", createdAt: Now.AddMinutes(-i));

        var first = await ListAsync(Query(pageSize: 10));
        var third = await ListAsync(Query(pageSize: 10, page: 3));
        var beyond = await ListAsync(Query(pageSize: 10, page: 99));

        Assert.Equal(23, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal("t00", first.Items[0].Slug);
        Assert.Equal(3, third.Items.Count);
        Assert.Equal(["t20", "t21", "t22"], third.Items.Select(i => i.Slug));
        Assert.Equal(3, beyond.Query.Page);
        Assert.Equal(third.Items.Select(i => i.Id), beyond.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task List_PagesNeverOverlapOrSkip_WhenThePrimarySortValuesTie()
    {
        var sameTime = new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);
        foreach (var slug in new[] { "delta", "alpha", "echo", "charlie", "bravo" })
            _central.AddTenant(slug, createdAt: sameTime);

        // Two rows per page (the service accepts any normalized size; the browser allowlist starts at 10).
        var seen = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await ListAsync(TenantListQuery.Default with
            {
                Sort = TenantListSortField.CreatedAt,
                Direction = TenantListSortDirection.Descending,
                PageSize = 2,
                Page = page
            });
            seen.AddRange(result.Items.Select(i => i.Slug));
        }

        // Ties on CreatedAt fall back to slug, then id: every row exactly once, in a stable order.
        Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo"], seen);
    }

    [Fact]
    public async Task List_SecondaryOrder_IsSlugThenId_ForEverySortColumn()
    {
        var at = new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);
        _central.AddTenant("zeta", name: "Same", createdAt: at, updatedAt: at);
        _central.AddTenant("beta", name: "Same", createdAt: at, updatedAt: at);
        _central.AddTenant("mu", name: "Same", createdAt: at, updatedAt: at);

        foreach (var sort in new[] { "name", "created", "updated", "status" })
        foreach (var dir in new[] { "asc", "desc" })
        {
            var page = await ListAsync(Query(sort: sort, dir: dir));
            Assert.Equal(["beta", "mu", "zeta"], page.Items.Select(i => i.Slug));
        }
    }

    // Sorting ----------------------------------------------------------------------------------------

    [Fact]
    public async Task List_SortsByEachAllowlistedColumn()
    {
        _central.AddTenant("b-slug", name: "Charlie", isActive: false, createdAt: Now.AddDays(-1), updatedAt: Now.AddDays(-5));
        _central.AddTenant("c-slug", name: "Alpha", createdAt: Now.AddDays(-3), updatedAt: Now.AddDays(-1));
        _central.AddTenant("a-slug", name: "Bravo", createdAt: Now.AddDays(-2), updatedAt: Now.AddDays(-3));

        Assert.Equal(["Alpha", "Bravo", "Charlie"], (await ListAsync(Query(sort: "name"))).Items.Select(i => i.Name));
        Assert.Equal(["Charlie", "Bravo", "Alpha"], (await ListAsync(Query(sort: "name", dir: "desc"))).Items.Select(i => i.Name));
        Assert.Equal(["a-slug", "b-slug", "c-slug"], (await ListAsync(Query(sort: "slug"))).Items.Select(i => i.Slug));
        Assert.Equal(["b-slug", "a-slug", "c-slug"], (await ListAsync(Query(sort: "created"))).Items.Select(i => i.Slug));
        Assert.Equal(["c-slug", "a-slug", "b-slug"], (await ListAsync(Query(sort: "updated"))).Items.Select(i => i.Slug));
        Assert.Equal("b-slug", (await ListAsync(Query(sort: "status", dir: "desc"))).Items[0].Slug);
        Assert.Equal("b-slug", (await ListAsync(Query(sort: "status", dir: "asc"))).Items[^1].Slug);
    }

    [Fact]
    public async Task List_IgnoresAnUnknownSortColumn()
    {
        _central.AddTenant("old", createdAt: Now.AddDays(-9));
        _central.AddTenant("new", createdAt: Now);

        var page = await ListAsync(Query(sort: "EncryptedConnectionString"));

        Assert.Equal(TenantListSortField.CreatedAt, page.Query.Sort);
        Assert.Equal(["new", "old"], page.Items.Select(i => i.Slug));
    }

    // Search and filters ----------------------------------------------------------------------------

    [Fact]
    public async Task List_SearchesNameSlugAndDomain_CaseInsensitively()
    {
        _central.AddTenant("kebap-house", name: "Kebap House");
        _central.AddTenant("sushi", name: "Sushi Bar");
        _central.AddTenant("pizza", name: "PIZZA place");

        Assert.Equal(["kebap-house"], (await ListAsync(Query(search: "KEBAP"))).Items.Select(i => i.Slug));
        Assert.Equal(["pizza"], (await ListAsync(Query(search: "pizza p"))).Items.Select(i => i.Slug));
        Assert.Equal(["sushi"], (await ListAsync(Query(search: "sushi.wasla"))).Items.Select(i => i.Slug));
        Assert.Empty((await ListAsync(Query(search: "%"))).Items);
        Assert.Empty((await ListAsync(Query(search: "_"))).Items);
    }

    [Fact]
    public async Task List_FiltersByStatusMigrationRecordAndPlan()
    {
        _central.AddTenant("active-pro", planCode: "Pro");
        _central.AddTenant("inactive-starter", isActive: false, planCode: "Starter");
        _central.AddTenant("failed", lastMigrationResult: SecretMarkers.MigrationFailureText, planCode: "Pro");
        _central.AddTenant("unrecorded", lastMigrationResult: null);

        Assert.Equal(["inactive-starter"], Slugs(await ListAsync(Query(status: "inactive"))));
        Assert.Equal(3, (await ListAsync(Query(status: "active"))).TotalCount);
        Assert.Equal(["failed"], Slugs(await ListAsync(Query(migration: "failed"))));
        Assert.Equal(["unrecorded"], Slugs(await ListAsync(Query(migration: "not-recorded"))));
        Assert.Equal(["active-pro", "inactive-starter"], Slugs(await ListAsync(Query(migration: "succeeded"))).Order());
        Assert.Equal(["active-pro", "failed"], Slugs(await ListAsync(Query(plan: "pro"))).Order());
        Assert.Equal(["unrecorded"], Slugs(await ListAsync(Query(plan: "none"))));
        Assert.Equal(["active-pro"], Slugs(await ListAsync(Query(plan: "pro", status: "active", migration: "succeeded"))));

        var failed = (await ListAsync(Query(migration: "failed"))).Items.Single();
        Assert.Equal(TenantMigrationRecord.Failed, failed.MigrationRecord);
        Assert.Equal("Pro", failed.PlanCode);
    }

    [Fact]
    public async Task List_DistinguishesAnEmptyRegistry_FromNoMatches()
    {
        var empty = await ListAsync(Query(search: "anything"));
        Assert.True(empty.RegistryIsEmpty);

        _central.AddTenant("present");
        var noMatch = await ListAsync(Query(search: "absent"));
        Assert.False(noMatch.RegistryIsEmpty);
        Assert.Empty(noMatch.Items);
        Assert.Equal(1, noMatch.TotalPages);
    }

    [Fact]
    public async Task List_CountsOnlyActiveNotRemovedDevices_PerTenant()
    {
        var a = _central.AddTenant("a");
        var b = _central.AddTenant("b");
        _central.AddDevice(a.Id, "online", true, Now.AddSeconds(-30));
        _central.AddDevice(a.Id, "offline", true, Now.AddHours(-2));
        _central.AddDevice(a.Id, "disabled", false, Now);
        _central.AddDevice(a.Id, "removed", true, Now, removedAt: Now.AddDays(-1));
        _central.AddDevice(b.Id, "b-online", true, Now);

        var items = (await ListAsync(Query(sort: "slug"))).Items;

        Assert.Equal((2, 1), (items[0].ActivePrintBridgeDevices, items[0].OnlinePrintBridgeDevices));
        Assert.Equal((1, 1), (items[1].ActivePrintBridgeDevices, items[1].OnlinePrintBridgeDevices));
    }

    // Query budget (no N+1, no fan-out) --------------------------------------------------------------

    [Fact]
    public async Task List_UsesAFixedNumberOfCentralQueries_WhateverThePageSize()
    {
        for (var i = 0; i < 60; i++)
        {
            var tenant = _central.AddTenant($"t{i:00}");
            _central.AddDevice(tenant.Id, "pc", true, Now);
        }

        var small = await CountedAsync(service => service.GetTenantListAsync(Query(pageSize: 10), CancellationToken.None));
        var large = await CountedAsync(service => service.GetTenantListAsync(Query(pageSize: 100), CancellationToken.None));

        Assert.Equal(3, small.Commands);
        Assert.Equal(3, large.Commands);
        Assert.Equal(60, large.Result.Items.Count);
        Assert.All(large.SqlText, sql => Assert.DoesNotContain("EncryptedConnectionString", sql, StringComparison.Ordinal));
        Assert.All(large.SqlText, sql => Assert.DoesNotContain("TokenHash", sql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overview_UsesFourCentralQueries_AndNeverSelectsSecrets()
    {
        for (var i = 0; i < 40; i++)
            _central.AddTenant($"t{i:00}");

        var overview = await CountedAsync(service => service.GetOverviewAsync(CancellationToken.None));

        Assert.Equal(4, overview.Commands);
        Assert.All(overview.SqlText, sql =>
        {
            Assert.DoesNotContain("EncryptedConnectionString", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("TokenHash", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("PasswordHash", sql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CentralService_HasNoWayToOpenATenantDatabase()
    {
        var parameters = typeof(CentralAdminTenantOperationsService).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(ITenantDbContextFactory));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(ITenantOperationalHealthReader));
    }

    // Overview ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Overview_CountsTenantsRegistrationsAndDevices_FromCentralData()
    {
        var a = _central.AddTenant("a");
        _central.AddTenant("b", isActive: false, lastMigrationResult: SecretMarkers.MigrationFailureText);
        _central.AddTenant("c", lastMigrationResult: null, createdAt: Now.AddDays(1));
        _central.AddRegistration("r1", PendingRegistrationStatus.AwaitingPayment);
        _central.AddRegistration("r2", PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: Now.AddHours(-5));
        _central.AddRegistration("r3", PendingRegistrationStatus.PaymentSucceeded, paymentSucceededAt: Now.AddHours(-1));
        _central.AddRegistration("r4", PendingRegistrationStatus.PaymentFailed);
        _central.AddRegistration("r5", PendingRegistrationStatus.Expired);
        _central.AddRegistration("r6", PendingRegistrationStatus.Cancelled);
        _central.AddRegistration("r7", PendingRegistrationStatus.Provisioned, a.Id);
        _central.AddDevice(a.Id, "online", true, Now.AddSeconds(-60));
        _central.AddDevice(a.Id, "stale", true, Now.AddSeconds(-61));
        _central.AddDevice(a.Id, "stale-edge", true, Now.AddMinutes(-5));
        _central.AddDevice(a.Id, "offline", true, Now.AddMinutes(-5).AddSeconds(-1));
        _central.AddDevice(a.Id, "never", true, null);
        _central.AddDevice(a.Id, "disabled", false, Now);
        _central.AddDevice(a.Id, "removed", true, Now, removedAt: Now);

        var overview = await Service().GetOverviewAsync(CancellationToken.None);

        Assert.Equal(new TenantRegistryCounts(3, 2, 1, 1, 1), overview.Tenants);
        Assert.Equal(1, overview.Registrations.AwaitingPayment);
        Assert.Equal(2, overview.Registrations.AwaitingProvisioning);
        Assert.Equal(Now.AddHours(-5), overview.Registrations.OldestAwaitingProvisioningSinceUtc);
        Assert.Equal(1, overview.Registrations.PaymentFailed);
        Assert.Equal(2, overview.Registrations.ExpiredOrCancelled);
        Assert.Equal(1, overview.Registrations.Provisioned);
        Assert.Equal(new PrintBridgeFleetCounts(Online: 1, Stale: 2, Offline: 1, NeverConnected: 1, Disabled: 1), overview.PrintBridge);
        Assert.Equal("c", overview.RecentTenants[0].Slug);
    }

    [Fact]
    public async Task Overview_OfAnEmptyRegistry_IsAllZero()
    {
        var overview = await Service().GetOverviewAsync(CancellationToken.None);

        Assert.Equal(new TenantRegistryCounts(0, 0, 0, 0, 0), overview.Tenants);
        Assert.Equal(0, overview.PrintBridge.Active);
        Assert.Empty(overview.RecentTenants);
    }

    // Detail -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Detail_ReturnsOnlyTheRequestedTenant_WithSafeFields()
    {
        var a = _central.AddTenant("a", planCode: "Pro", lastMigrationResult: SecretMarkers.MigrationFailureText);
        var b = _central.AddTenant("b");
        var registration = _central.AddRegistration("a", PendingRegistrationStatus.Provisioned, a.Id, paymentSucceededAt: Now.AddDays(-1));
        _central.AddRegistration("b", PendingRegistrationStatus.Provisioned, b.Id);
        _central.AddDevice(a.Id, "A kitchen", true, Now.AddSeconds(-10));
        _central.AddDevice(a.Id, "A removed", true, Now, removedAt: Now);
        _central.AddDevice(b.Id, "B kitchen", true, Now);

        var detail = await Service().GetTenantDetailAsync(a.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(a.Id, detail.Id);
        Assert.True(detail.DatabaseConfigured);
        Assert.Equal(TenantMigrationRecord.Failed, detail.MigrationRecord);
        Assert.Equal("Pro", detail.Membership!.PlanCode);
        Assert.Equal(registration.Id, detail.Registration!.Id);
        var device = Assert.Single(detail.PrintBridgeDevices);
        Assert.Equal("A kitchen", device.Name);
        Assert.Equal(PrintBridgeConnectionStatus.Connected, device.ConnectionStatus);

        var json = JsonSerializer.Serialize(detail);
        foreach (var secret in SecretMarkers.All)
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-pii@example.test", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_ForAnUnknownId_OrARegistrationId_IsNull()
    {
        var tenant = _central.AddTenant("a");
        var registration = _central.AddRegistration("a", PendingRegistrationStatus.Provisioned, tenant.Id);

        Assert.Null(await Service().GetTenantDetailAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await Service().GetTenantDetailAsync(registration.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Detail_ReportsAMissingConnectionString_AsNotConfigured()
    {
        var tenant = _central.AddTenant("half", encryptedConnectionString: string.Empty);

        var detail = await Service().GetTenantDetailAsync(tenant.Id, CancellationToken.None);

        Assert.False(detail!.DatabaseConfigured);
    }

    [Fact]
    public async Task Queries_HonourCancellation()
    {
        _central.AddTenant("a");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GetTenantListAsync(Query(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GetOverviewAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GetTenantDetailAsync(Guid.NewGuid(), cts.Token));
    }

    // Helpers ----------------------------------------------------------------------------------------

    private CentralAdminTenantOperationsService Service(bool counted = false) => new(_central.CreateContext(counted), _time);

    private async Task<TenantListPage> ListAsync(TenantListQuery query) =>
        await Service().GetTenantListAsync(query, CancellationToken.None);

    private async Task<(T Result, int Commands, IReadOnlyList<string> SqlText)> CountedAsync<T>(Func<CentralAdminTenantOperationsService, Task<T>> run)
    {
        _central.Counter.Reset();
        var result = await run(Service(counted: true));
        return (result, _central.Counter.Count, _central.Counter.Commands);
    }

    private static List<string> Slugs(TenantListPage page) => page.Items.Select(i => i.Slug).ToList();

    private static TenantListQuery Query(
        string? search = null, string? status = null, string? migration = null, string? plan = null,
        string? sort = null, string? dir = null, int? page = null, int? pageSize = null) =>
        TenantListQuery.Create(search, status, migration, plan, sort, dir, page, pageSize, Plans);

    internal sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }
}
