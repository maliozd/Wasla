using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.GuidedSetup;
using Wasla.Application.Tours;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Persistence.Tenant.Configurations;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.GuidedSetup;

public sealed class GuidedSetupServiceTests : IDisposable
{
    private static readonly GuidedSetupPosition PlatformsStart = new(GuidedSetupSections.PlatformConnections, "choose-platform");
    private static readonly GuidedSetupPosition PrintBridgeInstall = new(GuidedSetupSections.PrintBridge, "install-app");

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Clock _clock = new();
    private readonly TenantDatabases _tenants = new();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewUser_ReadsAsNotStarted_WithoutWritingARow()
    {
        await SeedAsync(_tenantA, _owner);

        var state = await Service().GetAsync(_tenantA, _owner, Ct);

        Assert.Equal(GuidedSetupState.NotStarted, state);
        Assert.Equal(0, await CountRowsAsync(_tenantA));
    }

    [Fact]
    public async Task Start_MovesNotStartedToInProgress_AtTheGivenPosition()
    {
        await SeedAsync(_tenantA, _owner);

        var result = await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);

        Assert.Equal(GuidedSetupOutcome.Applied, result.Outcome);
        Assert.Equal(GuidedSetupStatus.InProgress, result.State.Status);
        Assert.Equal(GuidedSetupSections.PlatformConnections, result.State.SectionKey);
        Assert.Equal("choose-platform", result.State.StepKey);
        Assert.Equal(_clock.UtcNow, result.State.StartedAtUtc);
        Assert.Equal(_clock.UtcNow, result.State.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.State.StartedAtUtc!.Value.Kind);
        AssertConsistent(result.State);
        Assert.Equal(result.State, await Service().GetAsync(_tenantA, _owner, Ct));
    }

    [Fact]
    public async Task StartingTwice_IsIdempotent_AndKeepsTheSavedPositionAndStartTime()
    {
        await SeedAsync(_tenantA, _owner);
        var first = await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var second = await Service().StartAsync(_tenantA, _owner, PrintBridgeInstall, Ct);

        Assert.Equal(GuidedSetupOutcome.Unchanged, second.Outcome);
        Assert.True(second.Succeeded);
        Assert.Equal(first.State, second.State);
        Assert.Equal(1, await CountRowsAsync(_tenantA));
    }

    [Fact]
    public async Task SaveProgress_PersistsStableKeys_AndPauseResumesAtTheSamePosition()
    {
        await SeedAsync(_tenantA, _owner);
        var started = await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        _clock.Advance(TimeSpan.FromMinutes(3));

        var saved = await Service().SaveProgressAsync(_tenantA, _owner, PrintBridgeInstall, Ct);

        Assert.Equal(GuidedSetupOutcome.Applied, saved.Outcome);
        Assert.Equal(GuidedSetupStatus.InProgress, saved.State.Status);
        Assert.Equal(GuidedSetupSections.PrintBridge, saved.State.SectionKey);
        Assert.Equal("install-app", saved.State.StepKey);
        Assert.Equal(started.State.StartedAtUtc, saved.State.StartedAtUtc);
        Assert.Equal(_clock.UtcNow, saved.State.UpdatedAtUtc);
        AssertConsistent(saved.State);

        // Pausing is leaving: a later visit (a new service and context) reads the same position.
        _clock.Advance(TimeSpan.FromHours(20));
        Assert.Equal(saved.State, await Service().GetAsync(_tenantA, _owner, Ct));

        // A section without steps is a valid position too.
        var sectionOnly = await Service().SaveProgressAsync(_tenantA, _owner, new GuidedSetupPosition(GuidedSetupSections.LiveScreenDemo), Ct);
        Assert.Equal(GuidedSetupOutcome.Applied, sectionOnly.Outcome);
        Assert.Null(sectionOnly.State.StepKey);
    }

    [Fact]
    public async Task SaveProgress_IsRejected_UnlessInProgress_AndWritesNothing()
    {
        await SeedAsync(_tenantA, _owner, _manager);
        var notStarted = await Service().SaveProgressAsync(_tenantA, _owner, PlatformsStart, Ct);
        Assert.Equal(GuidedSetupOutcome.InvalidTransition, notStarted.Outcome);
        Assert.Equal(GuidedSetupState.NotStarted, notStarted.State);
        Assert.Equal(0, await CountRowsAsync(_tenantA));

        await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        var completed = (await Service().CompleteAsync(_tenantA, _owner, Ct)).State;
        var afterCompleted = await Service().SaveProgressAsync(_tenantA, _owner, PrintBridgeInstall, Ct);
        Assert.Equal(GuidedSetupOutcome.InvalidTransition, afterCompleted.Outcome);
        Assert.Equal(completed, afterCompleted.State);

        var skipped = (await Service().SkipAsync(_tenantA, _manager, Ct)).State;
        var afterSkipped = await Service().SaveProgressAsync(_tenantA, _manager, PrintBridgeInstall, Ct);
        Assert.Equal(GuidedSetupOutcome.InvalidTransition, afterSkipped.Outcome);
        Assert.Equal(skipped, afterSkipped.State);
    }

    [Fact]
    public async Task Skip_FromNotStarted_RecordsOnlyTheSkip()
    {
        await SeedAsync(_tenantA, _owner);

        var result = await Service().SkipAsync(_tenantA, _owner, Ct);

        Assert.Equal(GuidedSetupOutcome.Applied, result.Outcome);
        Assert.Equal(GuidedSetupStatus.Skipped, result.State.Status);
        Assert.Equal(_clock.UtcNow, result.State.SkippedAtUtc);
        Assert.Null(result.State.StartedAtUtc);
        Assert.Null(result.State.CompletedAtUtc);
        Assert.Null(result.State.SectionKey);
        AssertConsistent(result.State);
    }

    [Fact]
    public async Task Skip_FromInProgress_KeepsTheStartAndLastPosition()
    {
        await SeedAsync(_tenantA, _owner);
        var started = await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await Service().SkipAsync(_tenantA, _owner, Ct);

        Assert.Equal(GuidedSetupOutcome.Applied, result.Outcome);
        Assert.Equal(GuidedSetupStatus.Skipped, result.State.Status);
        Assert.Equal(started.State.StartedAtUtc, result.State.StartedAtUtc);
        Assert.Equal(_clock.UtcNow, result.State.SkippedAtUtc);
        Assert.Equal(_clock.UtcNow, result.State.UpdatedAtUtc);
        Assert.Equal(GuidedSetupSections.PlatformConnections, result.State.SectionKey);
        Assert.Null(result.State.CompletedAtUtc);
        AssertConsistent(result.State);
    }

    [Fact]
    public async Task Complete_FromInProgress_RecordsCompletion()
    {
        await SeedAsync(_tenantA, _owner);
        var started = await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        _clock.Advance(TimeSpan.FromMinutes(12));

        var result = await Service().CompleteAsync(_tenantA, _owner, Ct);

        Assert.Equal(GuidedSetupOutcome.Applied, result.Outcome);
        Assert.Equal(GuidedSetupStatus.Completed, result.State.Status);
        Assert.Equal(started.State.StartedAtUtc, result.State.StartedAtUtc);
        Assert.Equal(_clock.UtcNow, result.State.CompletedAtUtc);
        Assert.Null(result.State.SkippedAtUtc);
        AssertConsistent(result.State);
    }

    [Fact]
    public async Task Complete_BeforeStarting_IsRejected()
    {
        await SeedAsync(_tenantA, _owner);

        var result = await Service().CompleteAsync(_tenantA, _owner, Ct);

        Assert.Equal(GuidedSetupOutcome.InvalidTransition, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(GuidedSetupState.NotStarted, result.State);
        Assert.Equal(0, await CountRowsAsync(_tenantA));
    }

    [Fact]
    public async Task RepeatedSkipAndComplete_AreUnchanged_AndKeepTheFirstTimestamps()
    {
        await SeedAsync(_tenantA, _owner, _manager);
        var skipped = await Service().SkipAsync(_tenantA, _owner, Ct);
        await Service().StartAsync(_tenantA, _manager, PlatformsStart, Ct);
        var completed = await Service().CompleteAsync(_tenantA, _manager, Ct);
        _clock.Advance(TimeSpan.FromDays(1));

        var skippedAgain = await Service().SkipAsync(_tenantA, _owner, Ct);
        var completedAgain = await Service().CompleteAsync(_tenantA, _manager, Ct);

        Assert.Equal(GuidedSetupOutcome.Unchanged, skippedAgain.Outcome);
        Assert.Equal(skipped.State, skippedAgain.State);
        Assert.Equal(GuidedSetupOutcome.Unchanged, completedAgain.Outcome);
        Assert.Equal(completed.State, completedAgain.State);
    }

    [Fact]
    public async Task TerminalStates_NeverChange_ThroughAnyCommand()
    {
        await SeedAsync(_tenantA, _owner, _manager);
        await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        var completed = (await Service().CompleteAsync(_tenantA, _owner, Ct)).State;
        var skipped = (await Service().SkipAsync(_tenantA, _manager, Ct)).State;
        _clock.Advance(TimeSpan.FromDays(3));

        var completedThenSkip = await Service().SkipAsync(_tenantA, _owner, Ct);
        var completedThenStart = await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        var skippedThenComplete = await Service().CompleteAsync(_tenantA, _manager, Ct);
        var skippedThenStart = await Service().StartAsync(_tenantA, _manager, PlatformsStart, Ct);

        Assert.All(
            new[] { completedThenSkip, completedThenStart, skippedThenComplete, skippedThenStart },
            result => Assert.Equal(GuidedSetupOutcome.InvalidTransition, result.Outcome));
        Assert.Equal(completed, await Service().GetAsync(_tenantA, _owner, Ct));
        Assert.Equal(skipped, await Service().GetAsync(_tenantA, _manager, Ct));
        Assert.Null(completed.SkippedAtUtc);
        Assert.Null(skipped.CompletedAtUtc);
    }

    [Theory]
    [InlineData("unknown-section", null)]
    [InlineData("", null)]
    [InlineData("Platform-Connections", null)]
    [InlineData(GuidedSetupSections.PlatformConnections, "Choose Platform")]
    [InlineData(GuidedSetupSections.PlatformConnections, "choose--platform")]
    [InlineData(GuidedSetupSections.PlatformConnections, "")]
    public async Task InvalidPositions_AreRejected_WithoutWriting(string section, string? step)
    {
        await SeedAsync(_tenantA, _owner);

        var start = await Service().StartAsync(_tenantA, _owner, new GuidedSetupPosition(section, step), Ct);
        Assert.Equal(GuidedSetupOutcome.InvalidPosition, start.Outcome);
        Assert.Equal(0, await CountRowsAsync(_tenantA));

        await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        var save = await Service().SaveProgressAsync(_tenantA, _owner, new GuidedSetupPosition(section, step), Ct);
        Assert.Equal(GuidedSetupOutcome.InvalidPosition, save.Outcome);
        Assert.Equal("choose-platform", save.State.StepKey);
    }

    [Fact]
    public async Task StepKeysLongerThanTheColumn_AreRejected()
    {
        await SeedAsync(_tenantA, _owner);
        var tooLong = new string('a', GuidedSetupSections.MaxKeyLength + 1);

        var result = await Service().StartAsync(_tenantA, _owner, new GuidedSetupPosition(GuidedSetupSections.PrintBridge, tooLong), Ct);

        Assert.Equal(GuidedSetupOutcome.InvalidPosition, result.Outcome);
    }

    [Fact]
    public async Task UsersInOneTenant_AreIndependent()
    {
        await SeedAsync(_tenantA, _owner, _manager);

        await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);
        await Service().CompleteAsync(_tenantA, _owner, Ct);

        Assert.Equal(GuidedSetupState.NotStarted, await Service().GetAsync(_tenantA, _manager, Ct));
        await Service().SkipAsync(_tenantA, _manager, Ct);
        Assert.Equal(GuidedSetupStatus.Completed, (await Service().GetAsync(_tenantA, _owner, Ct)).Status);
        Assert.Equal(GuidedSetupStatus.Skipped, (await Service().GetAsync(_tenantA, _manager, Ct)).Status);
    }

    [Fact]
    public async Task TheSameUserIdInAnotherTenant_IsIsolated()
    {
        await SeedAsync(_tenantA, _owner);
        await SeedAsync(_tenantB, _owner);

        await Service().SkipAsync(_tenantA, _owner, Ct);

        Assert.Equal(GuidedSetupState.NotStarted, await Service().GetAsync(_tenantB, _owner, Ct));
        Assert.Equal(0, await CountRowsAsync(_tenantB));
        var started = await Service().StartAsync(_tenantB, _owner, PlatformsStart, Ct);
        Assert.Equal(GuidedSetupOutcome.Applied, started.Outcome);
        Assert.Equal(GuidedSetupStatus.Skipped, (await Service().GetAsync(_tenantA, _owner, Ct)).Status);
    }

    [Fact]
    public async Task ConcurrentStarts_CreateExactlyOneRow()
    {
        await SeedAsync(_tenantA, _owner);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Task.Run(() => Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct), Ct)));

        Assert.Equal(1, await CountRowsAsync(_tenantA));
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(results.Select(result => result.State).Distinct());
        Assert.Equal(GuidedSetupStatus.InProgress, results[0].State.Status);
    }

    [Fact]
    public async Task ConcurrentSkipAndComplete_OneWins_AndNeverBoth()
    {
        for (var round = 0; round < 8; round++)
        {
            var user = Guid.NewGuid();
            await SeedAsync(_tenantA, user);
            await Service().StartAsync(_tenantA, user, PlatformsStart, Ct);

            var skip = Task.Run(() => Service().SkipAsync(_tenantA, user, Ct), Ct);
            var complete = Task.Run(() => Service().CompleteAsync(_tenantA, user, Ct), Ct);
            var results = await Task.WhenAll(skip, complete);

            Assert.Single(results, result => result.Outcome == GuidedSetupOutcome.Applied);
            Assert.Single(results, result => result.Outcome == GuidedSetupOutcome.InvalidTransition);
            var final = await Service().GetAsync(_tenantA, user, Ct);
            Assert.True(final.CompletedAtUtc is null || final.SkippedAtUtc is null);
            AssertConsistent(final);
        }
    }

    [Fact]
    public async Task LosingTheInsertRace_ToAStart_ReturnsTheWinnersProgress()
    {
        await SeedAsync(_tenantA, _owner);
        // Another tab starts first, after this request decided to insert and before its insert runs.
        _tenants.BeforeGuidedSetupInsert = () => InsertRowAsync(_tenantA, _owner, GuidedSetupStatus.InProgress, PrintBridgeInstall);

        var result = await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct);

        Assert.Equal(GuidedSetupOutcome.Unchanged, result.Outcome);
        Assert.Equal(GuidedSetupSections.PrintBridge, result.State.SectionKey);
        Assert.Equal(1, await CountRowsAsync(_tenantA));
    }

    [Fact]
    public async Task LosingTheInsertRace_ToACompletion_NeverRecordsASkip()
    {
        await SeedAsync(_tenantA, _owner);
        _tenants.BeforeGuidedSetupInsert = () => InsertRowAsync(_tenantA, _owner, GuidedSetupStatus.Completed, PlatformsStart);

        var result = await Service().SkipAsync(_tenantA, _owner, Ct);

        Assert.Equal(GuidedSetupOutcome.InvalidTransition, result.Outcome);
        var stored = await Service().GetAsync(_tenantA, _owner, Ct);
        Assert.Equal(GuidedSetupStatus.Completed, stored.Status);
        Assert.Null(stored.SkippedAtUtc);
    }

    [Fact]
    public async Task EarlierProductTourCompletions_DoNotInitializeGuidedSetup()
    {
        await SeedAsync(_tenantA, _owner, _manager);
        // Earlier guided-demo and screen tours, finished or skipped, are not guided-setup state.
        await AddTourCompletionAsync(_tenantA, _owner, ProductTourKeys.GuidedDemo, _clock.UtcNow.AddDays(-3));
        await AddTourCompletionAsync(_tenantA, _owner, ProductTourKeys.LiveScreenIntro, _clock.UtcNow.AddDays(-3));
        await AddTourCompletionAsync(_tenantA, _manager, ProductTourKeys.GuidedDemo, _clock.UtcNow.AddDays(-2));

        Assert.Equal(GuidedSetupState.NotStarted, await Service().GetAsync(_tenantA, _owner, Ct));
        Assert.Equal(GuidedSetupState.NotStarted, await Service().GetAsync(_tenantA, _manager, Ct));
        Assert.Equal(0, await CountRowsAsync(_tenantA));

        // They get the new Start-or-Skip decision like anyone else, and the old rows stay untouched.
        Assert.Equal(GuidedSetupOutcome.Applied, (await Service().StartAsync(_tenantA, _owner, PlatformsStart, Ct)).Outcome);
        Assert.Equal(GuidedSetupOutcome.Applied, (await Service().SkipAsync(_tenantA, _manager, Ct)).Outcome);
        Assert.Equal(3, await CountTourCompletionsAsync(_tenantA));
    }

    [Fact]
    public async Task ReadingState_NeverWritesToTheDatabase()
    {
        await SeedAsync(_tenantA, _owner, _manager);
        await AddTourCompletionAsync(_tenantA, _owner, ProductTourKeys.GuidedDemo, _clock.UtcNow);
        await Service().StartAsync(_tenantA, _manager, PlatformsStart, Ct);
        var managerBefore = await Service().GetAsync(_tenantA, _manager, Ct);

        var commands = await _tenants.RecordCommandsAsync(async () =>
        {
            await Service().GetAsync(_tenantA, _owner, Ct);   // missing state, legacy tour row present
            await Service().GetAsync(_tenantA, _manager, Ct); // existing state
        });

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.StartsWith("SELECT", command.TrimStart(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, await CountRowsAsync(_tenantA));
        Assert.Equal(managerBefore, await Service().GetAsync(_tenantA, _manager, Ct));
    }

    [Fact]
    public async Task ACancelledRequest_StopsWithoutWriting()
    {
        await SeedAsync(_tenantA, _owner);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().StartAsync(_tenantA, _owner, PlatformsStart, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().SkipAsync(_tenantA, _owner, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GetAsync(_tenantA, _owner, cancelled.Token));

        Assert.Equal(0, await CountRowsAsync(_tenantA));
    }

    [Fact]
    public async Task Database_RejectsASecondRowForTheSameUser()
    {
        await SeedAsync(_tenantA, _owner);
        await InsertRowAsync(_tenantA, _owner, GuidedSetupStatus.InProgress, PlatformsStart);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            InsertRowAsync(_tenantA, _owner, GuidedSetupStatus.Skipped, null));

        Assert.True(GuidedSetupService.IsUserStateConflict(error));
    }

    [Fact]
    public void ConflictDetection_RecognizesTheSqlServerIndex_AndIgnoresOtherErrors()
    {
        var sqlServer = new DbUpdateException("update failed", new Exception(
            "Cannot insert duplicate key row in object 'dbo.UserGuidedSetupStates' with unique index 'IX_UserGuidedSetupStates_UserId'. The duplicate key value is (x)."));
        var otherIndex = new DbUpdateException("update failed", new Exception(
            "Cannot insert duplicate key row in object 'dbo.GuidedDemoSessions' with unique index 'IX_GuidedDemoSessions_UserId_Open'."));
        var foreignKey = new DbUpdateException("update failed", new Exception(
            "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_UserGuidedSetupStates_AppUsers_UserId\"."));

        Assert.True(GuidedSetupService.IsUserStateConflict(sqlServer));
        Assert.False(GuidedSetupService.IsUserStateConflict(otherIndex));
        Assert.False(GuidedSetupService.IsUserStateConflict(foreignKey));
    }

    [Fact]
    public void SqlServerModel_HasOneUniqueRowPerUser()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True")
            .Options;
        using var db = new TenantDbContext(options);

        var entity = db.Model.FindEntityType(typeof(UserGuidedSetupState))!;
        var index = Assert.Single(entity.GetIndexes());

        Assert.Equal("UserGuidedSetupStates", entity.GetTableName());
        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
        Assert.Equal(UserGuidedSetupStateIndexes.User, index.GetDatabaseName());
        Assert.Equal(nameof(UserGuidedSetupState.UserId), Assert.Single(index.Properties).Name);
        Assert.Equal(GuidedSetupSections.MaxKeyLength, entity.FindProperty(nameof(UserGuidedSetupState.CurrentSectionKey))!.GetMaxLength());
        Assert.Equal(GuidedSetupSections.MaxKeyLength, entity.FindProperty(nameof(UserGuidedSetupState.CurrentStepKey))!.GetMaxLength());
    }

    private GuidedSetupService Service() => new(_tenants, _clock);

    private static void AssertConsistent(GuidedSetupState state)
    {
        switch (state.Status)
        {
            case GuidedSetupStatus.NotStarted:
                Assert.Equal(GuidedSetupState.NotStarted, state);
                break;
            case GuidedSetupStatus.InProgress:
                Assert.NotNull(state.StartedAtUtc);
                Assert.NotNull(state.SectionKey);
                Assert.Null(state.CompletedAtUtc);
                Assert.Null(state.SkippedAtUtc);
                break;
            case GuidedSetupStatus.Completed:
                // Completion is only reachable from InProgress, so it always has a start.
                Assert.NotNull(state.StartedAtUtc);
                Assert.NotNull(state.CompletedAtUtc);
                Assert.Null(state.SkippedAtUtc);
                Assert.True(state.StartedAtUtc <= state.CompletedAtUtc);
                break;
            case GuidedSetupStatus.Skipped:
                Assert.NotNull(state.SkippedAtUtc);
                Assert.Null(state.CompletedAtUtc);
                Assert.True(state.StartedAtUtc is null || state.StartedAtUtc <= state.SkippedAtUtc);
                break;
        }

        if (state.Status != GuidedSetupStatus.NotStarted)
        {
            Assert.NotNull(state.UpdatedAtUtc);
            foreach (var moment in new[] { state.StartedAtUtc, state.CompletedAtUtc, state.SkippedAtUtc })
                Assert.True(moment is null || moment <= state.UpdatedAtUtc);
        }
    }

    private async Task SeedAsync(Guid tenantId, params Guid[] userIds)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        foreach (var userId in userIds)
        {
            db.AppUsers.Add(new AppUser
            {
                Id = userId,
                Email = userId.ToString("N") + "@example.test",
                PasswordHash = "hash",
                FullName = "Guided Setup User",
                Role = UserRole.Owner,
                IsActive = true
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task AddTourCompletionAsync(Guid tenantId, Guid userId, string tourKey, DateTime completedAtUtc)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        db.UserProductTourCompletions.Add(new UserProductTourCompletion
        {
            UserId = userId,
            TourKey = tourKey,
            CompletedAtUtc = completedAtUtc
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task InsertRowAsync(Guid tenantId, Guid userId, GuidedSetupStatus status, GuidedSetupPosition? position)
    {
        var now = _clock.UtcNow;
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None, withHooks: false);
        db.UserGuidedSetupStates.Add(new UserGuidedSetupState
        {
            UserId = userId,
            Status = status,
            CurrentSectionKey = position?.SectionKey,
            CurrentStepKey = position?.StepKey,
            StartedAtUtc = status == GuidedSetupStatus.Skipped ? null : now,
            CompletedAtUtc = status == GuidedSetupStatus.Completed ? now : null,
            SkippedAtUtc = status == GuidedSetupStatus.Skipped ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> CountRowsAsync(Guid tenantId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None, withHooks: false);
        return await db.UserGuidedSetupStates.CountAsync(Ct);
    }

    private async Task<int> CountTourCompletionsAsync(Guid tenantId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None, withHooks: false);
        return await db.UserProductTourCompletions.CountAsync(Ct);
    }

    public void Dispose() => _tenants.Dispose();

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

        public DateTime UtcNow => _now.UtcDateTime;

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// One SQLite file per tenant, a new connection per context, so parallel requests really race
    /// on the database like separate web requests do.
    /// </summary>
    internal sealed class TenantDatabases : ITenantDbContextFactory, IDisposable
    {
        private readonly string _directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "wasla-guided-setup-" + Guid.NewGuid().ToString("N"))).FullName;
        private readonly HashSet<Guid> _created = new();
        private readonly object _gate = new();
        private readonly OwnedSqlitePools _pools = new();

        private List<string>? _recorded;

        /// <summary>Runs once, inside the next guided-setup insert, just before it reaches the database.</summary>
        public Func<Task>? BeforeGuidedSetupInsert { get; set; }

        /// <summary>Every SQL command the service's contexts send while <paramref name="action"/> runs.</summary>
        public async Task<IReadOnlyList<string>> RecordCommandsAsync(Func<Task> action)
        {
            var recorded = new List<string>();
            lock (_gate)
                _recorded = recorded;
            try
            {
                await action();
            }
            finally
            {
                lock (_gate)
                    _recorded = null;
            }

            lock (_gate)
                return recorded.ToList();
        }

        private void Record(string commandText)
        {
            lock (_gate)
                _recorded?.Add(commandText);
        }

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct) =>
            CreateAsync(customerId, ct, withHooks: true);

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct, bool withHooks)
        {
            var connectionString = _pools.Own(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, customerId.ToString("N") + ".db"),
                DefaultTimeout = 30
            }.ToString());

            lock (_gate)
            {
                if (_created.Add(customerId))
                {
                    using var setup = new GuidedSetupTenantDbContext(Options(connectionString, []));
                    setup.Database.EnsureCreated();
                    setup.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                }
            }

            IInterceptor[] interceptors = withHooks ? [new InsertRace(this), new CommandRecorder(this)] : [];
            return Task.FromResult<TenantDbContext>(new GuidedSetupTenantDbContext(Options(connectionString, interceptors)));
        }

        public void Dispose()
        {
            _pools.Clear();
            Directory.Delete(_directory, recursive: true);
        }

        private Func<Task>? TakeHook()
        {
            lock (_gate)
            {
                var hook = BeforeGuidedSetupInsert;
                BeforeGuidedSetupInsert = null;
                return hook;
            }
        }

        private static DbContextOptions<TenantDbContext> Options(string connectionString, IInterceptor[] interceptors)
        {
            var builder = new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString);
            if (interceptors.Length > 0)
                builder.AddInterceptors(interceptors);
            return builder.Options;
        }

        private sealed class CommandRecorder(TenantDatabases owner) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                owner.Record(command.CommandText);
                return ValueTask.FromResult(result);
            }

            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                owner.Record(command.CommandText);
                return ValueTask.FromResult(result);
            }

            public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
            {
                owner.Record(command.CommandText);
                return ValueTask.FromResult(result);
            }
        }

        private sealed class InsertRace(TenantDatabases owner) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                var inserting = eventData.Context?.ChangeTracker.Entries<UserGuidedSetupState>()
                    .Any(entry => entry.State == EntityState.Added) == true;
                if (inserting && owner.TakeHook() is { } hook)
                    await hook();
                return result;
            }
        }
    }

    /// <summary>The real guided-setup and tour configurations with a minimal user table for SQLite.</summary>
    private sealed class GuidedSetupTenantDbContext : TenantDbContext
    {
        public GuidedSetupTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppUser>(builder =>
            {
                builder.ToTable("AppUsers");
                builder.HasKey(user => user.Id);
                builder.Property(user => user.Email).IsRequired();
                builder.Property(user => user.PasswordHash).IsRequired();
            });
            modelBuilder.ApplyConfiguration(new UserProductTourCompletionConfiguration());
            modelBuilder.ApplyConfiguration(new UserGuidedSetupStateConfiguration());
        }
    }
}
