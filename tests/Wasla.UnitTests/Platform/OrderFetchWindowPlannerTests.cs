using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Platform;

public sealed class OrderFetchWindowPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public void WithoutCheckpoint_LooksBackTheInitialLookbackOnly()
    {
        var plan = OrderFetchWindowPlanner.Plan(null, Now, Hour);

        var window = Assert.Single(plan.Windows);
        Assert.Equal(Now - OrderFetchWindowPlanner.InitialLookback, window.StartUtc);
        Assert.Equal(Now, window.EndUtc);
        Assert.Equal(TimeSpan.FromHours(1), OrderFetchWindowPlanner.InitialLookback);
        Assert.False(plan.HasMore);
    }

    [Fact]
    public void WithCheckpoint_StartsTheOverlapBeforeIt_AndEndsNow()
    {
        var checkpoint = Now.AddSeconds(-30);

        var plan = OrderFetchWindowPlanner.Plan(checkpoint, Now, Hour);

        var window = Assert.Single(plan.Windows);
        Assert.Equal(checkpoint - OrderFetchWindowPlanner.CheckpointOverlap, window.StartUtc);
        Assert.Equal(Now, window.EndUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), OrderFetchWindowPlanner.CheckpointOverlap);
    }

    [Fact]
    public void CheckpointReadBackWithoutKind_IsTreatedAsUtc()
    {
        var stored = DateTime.SpecifyKind(Now.AddMinutes(-2), DateTimeKind.Unspecified);

        var window = Assert.Single(OrderFetchWindowPlanner.Plan(stored, Now, Hour).Windows);

        Assert.Equal(DateTimeKind.Utc, window.StartUtc.Kind);
        Assert.Equal(Now.AddMinutes(-7), window.StartUtc);
    }

    [Fact]
    public void CheckpointAheadOfTheClock_DoesNotSkipTheCurrentInterval()
    {
        var plan = OrderFetchWindowPlanner.Plan(Now.AddHours(3), Now, Hour);

        var window = Assert.Single(plan.Windows);
        Assert.Equal(Now - OrderFetchWindowPlanner.CheckpointOverlap, window.StartUtc);
        Assert.Equal(Now, window.EndUtc);
    }

    [Fact]
    public void OutageLongerThanOneWindow_IsSplitIntoConsecutiveWindowsWithoutGaps()
    {
        var checkpoint = Now.AddHours(-5);

        var plan = OrderFetchWindowPlanner.Plan(checkpoint, Now, Hour);

        Assert.Equal(6, plan.Windows.Count);
        Assert.False(plan.HasMore);
        Assert.Equal(checkpoint - OrderFetchWindowPlanner.CheckpointOverlap, plan.Windows[0].StartUtc);
        Assert.Equal(Now, plan.Windows[^1].EndUtc);
        for (var i = 0; i < plan.Windows.Count; i++)
        {
            Assert.True(plan.Windows[i].EndUtc - plan.Windows[i].StartUtc <= Hour);
            Assert.True(plan.Windows[i].EndUtc > plan.Windows[i].StartUtc);
            if (i > 0)
                Assert.Equal(plan.Windows[i - 1].EndUtc, plan.Windows[i].StartUtc);
        }
    }

    [Fact]
    public void OutageLongerThanOneRun_StopsAtTheRunBudget_AndReportsMore()
    {
        var checkpoint = Now.AddDays(-3);

        var plan = OrderFetchWindowPlanner.Plan(checkpoint, Now, Hour);

        Assert.Equal(OrderFetchWindowPlanner.MaxWindowsPerRun, plan.Windows.Count);
        Assert.True(plan.HasMore);
        Assert.Equal(checkpoint - OrderFetchWindowPlanner.CheckpointOverlap, plan.Windows[0].StartUtc);
        Assert.Equal(plan.Windows[0].StartUtc + Hour * OrderFetchWindowPlanner.MaxWindowsPerRun, plan.Windows[^1].EndUtc);
    }

    [Fact]
    public void SamePlanForTheSameInputs()
    {
        var checkpoint = Now.AddHours(-30).AddMinutes(-17);

        var first = OrderFetchWindowPlanner.Plan(checkpoint, Now, Hour);
        var second = OrderFetchWindowPlanner.Plan(checkpoint, Now, Hour);

        Assert.Equal(first.Windows, second.Windows);
        Assert.Equal(first.HasMore, second.HasMore);
    }

    [Fact]
    public void ClientWithoutMaximumWindow_GetsOneWindowForTheWholeInterval()
    {
        var checkpoint = Now.AddDays(-3);

        var plan = OrderFetchWindowPlanner.Plan(checkpoint, Now, maxWindow: null);

        var window = Assert.Single(plan.Windows);
        Assert.Equal(checkpoint - OrderFetchWindowPlanner.CheckpointOverlap, window.StartUtc);
        Assert.Equal(Now, window.EndUtc);
        Assert.False(plan.HasMore);
    }
}
