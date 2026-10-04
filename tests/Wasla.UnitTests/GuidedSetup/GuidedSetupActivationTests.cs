using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.GuidedSetup;

/// <summary>
/// Guided setup is per user; the operational mode is per tenant. Starting never changes the tenant. Completing or
/// skipping takes a Setup tenant live in the same transaction as the user's own change, idempotently, and nothing a
/// later user does returns a Live tenant to Setup.
/// </summary>
public sealed class GuidedSetupActivationTests : IDisposable
{
    private static readonly GuidedSetupPosition TrainingStart = new(GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.Intro);
    private static readonly GuidedSetupPosition PlatformsStart = new(GuidedSetupSections.PlatformConnections, GuidedTrainingSteps.Intro);

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly OperationalModeTestClock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _employee = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartingTraining_LeavesTheTenantInSetup()
    {
        await NewTenantAsync(_tenant, _owner);

        var started = await Service().StartAsync(_tenant, _owner, TrainingStart, Ct);
        await Service().SaveProgressAsync(_tenant, _owner, new GuidedSetupPosition(GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeNew), Ct);

        Assert.Equal(GuidedSetupOutcome.Applied, started.Outcome);
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenant));
    }

    [Fact]
    public async Task CompletingTraining_TakesTheTenantLive()
    {
        await NewTenantAsync(_tenant, _owner);
        await Service().StartAsync(_tenant, _owner, TrainingStart, Ct);

        var completed = await Service().CompleteAsync(_tenant, _owner, Ct);

        Assert.Equal(GuidedSetupOutcome.Applied, completed.Outcome);
        Assert.Equal(GuidedSetupStatus.Completed, completed.State.Status);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
    }

    [Fact]
    public async Task SkippingBeforeStarting_TakesTheTenantLiveImmediately()
    {
        await NewTenantAsync(_tenant, _owner);

        var skipped = await Service().SkipAsync(_tenant, _owner, Ct);

        Assert.Equal(GuidedSetupOutcome.Applied, skipped.Outcome);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
    }

    [Fact]
    public async Task EndingAStartedJourney_IsAPermanentSkip_AndTakesTheTenantLive()
    {
        await NewTenantAsync(_tenant, _owner);
        await Service().StartAsync(_tenant, _owner, PlatformsStart, Ct);

        var ended = await Service().SkipAsync(_tenant, _owner, Ct);

        Assert.Equal(GuidedSetupStatus.Skipped, ended.State.Status);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
    }

    [Fact]
    public async Task Activation_IsIdempotent_AndLaterFinishesKeepTheTenantLive()
    {
        await NewTenantAsync(_tenant, _owner, _employee);
        await Service().SkipAsync(_tenant, _owner, Ct);
        var liveSince = await SettingsUpdatedAtAsync(_tenant);
        _clock.Now = _clock.Now.AddMinutes(10);

        Assert.Equal(GuidedSetupOutcome.Unchanged, (await Service().SkipAsync(_tenant, _owner, Ct)).Outcome);
        await Service().StartAsync(_tenant, _employee, TrainingStart, Ct);
        await Service().CompleteAsync(_tenant, _employee, Ct);

        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        Assert.Equal(liveSince, await SettingsUpdatedAtAsync(_tenant)); // nothing rewrote the Live tenant
    }

    [Fact]
    public async Task ALaterUser_NeverReturnsALiveTenantToSetup()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live, autoApprove: true);
        await _tenants.SeedUserAsync(_tenant, _employee, UserRole.Kitchen);

        await Service().StartAsync(_tenant, _employee, TrainingStart, Ct);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        await Service().SkipAsync(_tenant, _employee, Ct);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));

        var another = Guid.NewGuid();
        await _tenants.SeedUserAsync(_tenant, another, UserRole.Manager);
        await Service().StartAsync(_tenant, another, TrainingStart, Ct);
        await Service().CompleteAsync(_tenant, another, Ct);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
    }

    [Fact]
    public async Task ConcurrentFinishesByDifferentUsers_AllSucceed_AndTheTenantGoesLiveOnce()
    {
        var users = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        await NewTenantAsync(_tenant, users);
        for (var i = 0; i < users.Length; i += 2)
            await Service().StartAsync(_tenant, users[i], TrainingStart, Ct);

        var results = await Task.WhenAll(users.Select((user, i) => Task.Run(() => i % 2 == 0
            ? Service().CompleteAsync(_tenant, user, Ct)
            : Service().SkipAsync(_tenant, user, Ct), Ct)));

        Assert.All(results, result => Assert.Equal(GuidedSetupOutcome.Applied, result.Outcome));
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(1, await db.TenantOperationalSettings.CountAsync(Ct));
    }

    [Fact]
    public async Task IfTheTenantCannotGoLive_TheCompletionIsRolledBack()
    {
        await NewTenantAsync(_tenant, _owner);
        await Service().StartAsync(_tenant, _owner, TrainingStart, Ct);
        _tenants.FailNextStatementContaining = "UPDATE \"TenantOperationalSettings\"";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().CompleteAsync(_tenant, _owner, Ct));

        // Never "finished" while the tenant stays blocked in Setup: the user can simply complete again.
        Assert.Equal(GuidedSetupStatus.InProgress, (await Service().GetAsync(_tenant, _owner, Ct)).Status);
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenant));
        Assert.Equal(GuidedSetupOutcome.Applied, (await Service().CompleteAsync(_tenant, _owner, Ct)).Outcome);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
    }

    [Fact]
    public async Task IfTheTenantCannotGoLive_TheFirstSkipIsRolledBack()
    {
        await NewTenantAsync(_tenant, _owner);
        _tenants.FailNextStatementContaining = "UPDATE \"TenantOperationalSettings\"";

        await Assert.ThrowsAnyAsync<Exception>(() => Service().SkipAsync(_tenant, _owner, Ct));

        Assert.Equal(GuidedSetupStatus.NotStarted, (await Service().GetAsync(_tenant, _owner, Ct)).Status);
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenant));
    }

    [Fact]
    public async Task OneTenantGoingLive_NeverChangesAnotherTenant()
    {
        await NewTenantAsync(_tenant, _owner);
        await NewTenantAsync(_otherTenant, _owner); // the same user id in another tenant

        await Service().SkipAsync(_tenant, _owner, Ct);

        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_otherTenant));
        Assert.Equal(GuidedSetupStatus.NotStarted, (await Service().GetAsync(_otherTenant, _owner, Ct)).Status);
    }

    public void Dispose() => _tenants.Dispose();

    private GuidedSetupService Service() => new(_tenants, _clock);

    /// <summary>A tenant as provisioning leaves it: in Setup, with its users.</summary>
    private async Task NewTenantAsync(Guid tenantId, params Guid[] users)
    {
        await using (var db = await _tenants.CreateAsync(tenantId, CancellationToken.None))
            await TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(db, _clock.UtcNow, CancellationToken.None);
        foreach (var user in users)
            await _tenants.SeedUserAsync(tenantId, user);
    }

    private async Task<DateTime> SettingsUpdatedAtAsync(Guid tenantId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        return (await db.TenantOperationalSettings.SingleAsync()).UpdatedAt;
    }
}
