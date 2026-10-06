using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Platform;

public sealed class OrderFetchWindowPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Overlap = OrderFetchWindowPlanner.CheckpointOverlap;

    [Fact]
    public void WithoutCheckpoint_LooksBackTheInitialLookbackOnly_AndAdvances()
    {
        var plan = OrderFetchWindowPlanner.PlanCurrent(null, Now, Hour);

        Assert.Equal(Now - OrderFetchWindowPlanner.InitialLookback, plan.Window.StartUtc);
        Assert.Equal(Now, plan.Window.EndUtc);
        Assert.True(plan.AdvancesCheckpoint);
        Assert.Equal(TimeSpan.FromHours(1), OrderFetchWindowPlanner.InitialLookback);
        Assert.True(OrderFetchWindowPlanner.PlanCurrent(null, Now, Hour).AdvancesCheckpoint);
    }

    [Fact]
    public void RecentCheckpoint_CoversEverythingSinceTheOverlap_AndAdvances()
    {
        var checkpoint = Now.AddSeconds(-30);

        var plan = OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, Hour);

        Assert.Equal(checkpoint - Overlap, plan.Window.StartUtc);
        Assert.Equal(Now, plan.Window.EndUtc);
        Assert.True(plan.AdvancesCheckpoint);
        Assert.Equal(TimeSpan.FromMinutes(5), Overlap);
    }

    [Fact]
    public void CheckpointExactlyOneWindowBack_StillFitsOneWindow()
    {
        var checkpoint = Now - Hour + Overlap;

        var plan = OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, Hour);

        Assert.True(plan.AdvancesCheckpoint);
        Assert.Equal(Now - Hour, plan.Window.StartUtc);
        Assert.True(OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, Hour).AdvancesCheckpoint);
    }

    [Fact]
    public void OldCheckpoint_FetchesTheHotWindowFirst_WithoutAdvancing()
    {
        var checkpoint = Now.AddHours(-6);

        var plan = OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, Hour);

        Assert.Equal(Now - Hour, plan.Window.StartUtc);
        Assert.Equal(Now, plan.Window.EndUtc);
        Assert.False(plan.AdvancesCheckpoint);
        Assert.False(OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, Hour).AdvancesCheckpoint);
    }

    [Fact]
    public void CheckpointReadBackWithoutKind_IsTreatedAsUtc()
    {
        var stored = DateTime.SpecifyKind(Now.AddMinutes(-2), DateTimeKind.Unspecified);

        var plan = OrderFetchWindowPlanner.PlanCurrent(stored, Now, Hour);

        Assert.Equal(DateTimeKind.Utc, plan.Window.StartUtc.Kind);
        Assert.Equal(Now.AddMinutes(-7), plan.Window.StartUtc);
    }

    [Fact]
    public void CheckpointAheadOfTheClock_DoesNotSkipTheCurrentInterval()
    {
        var plan = OrderFetchWindowPlanner.PlanCurrent(Now.AddHours(3), Now, Hour);

        Assert.Equal(Now - Overlap, plan.Window.StartUtc);
        Assert.Equal(Now, plan.Window.EndUtc);
        Assert.True(plan.AdvancesCheckpoint);
    }

    [Fact]
    public void ClientWithoutMaximumWindow_GetsOneWindowForTheWholeInterval_AndNeverBackfills()
    {
        var checkpoint = Now.AddDays(-3);

        var plan = OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, maxWindow: null);

        Assert.Equal(checkpoint - Overlap, plan.Window.StartUtc);
        Assert.Equal(Now, plan.Window.EndUtc);
        Assert.True(plan.AdvancesCheckpoint);
        Assert.True(OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, maxWindow: null).AdvancesCheckpoint);
    }

    [Fact]
    public void Backfill_StartsAtTheCheckpointMinusTheOverlap_OldestFirst_WithoutGaps()
    {
        var checkpoint = Now.AddHours(-5);

        var windows = OrderFetchWindowPlanner.PlanBackfill(checkpoint, Now, Hour, maxWindows: 10);

        Assert.Equal(6, windows.Count);
        Assert.Equal(checkpoint - Overlap, windows[0].StartUtc);
        Assert.Equal(Now, windows[^1].EndUtc);
        for (var i = 0; i < windows.Count; i++)
        {
            Assert.True(windows[i].EndUtc > windows[i].StartUtc);
            Assert.True(windows[i].EndUtc - windows[i].StartUtc <= Hour);
            if (i > 0)
                Assert.Equal(windows[i - 1].EndUtc, windows[i].StartUtc);
        }
    }

    [Fact]
    public void Backfill_StopsAtTheRequestedNumberOfWindows()
    {
        var checkpoint = Now.AddDays(-3);

        var windows = OrderFetchWindowPlanner.PlanBackfill(checkpoint, Now, Hour, maxWindows: 1);

        var window = Assert.Single(windows);
        Assert.Equal(checkpoint - Overlap, window.StartUtc);
        Assert.Equal(checkpoint - Overlap + Hour, window.EndUtc);
    }

    [Fact]
    public void SamePlanForTheSameInputs()
    {
        var checkpoint = Now.AddHours(-30).AddMinutes(-17);

        Assert.Equal(
            OrderFetchWindowPlanner.PlanBackfill(checkpoint, Now, Hour, 12),
            OrderFetchWindowPlanner.PlanBackfill(checkpoint, Now, Hour, 12));
        Assert.Equal(
            OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, Hour),
            OrderFetchWindowPlanner.PlanCurrent(checkpoint, Now, Hour));
    }

    [Fact]
    public void RecoveryWorkloadCap_IsTwelveWindowsPerCycle()
    {
        Assert.Equal(12, OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle);
    }
}
