using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.GuidedSetup;

public sealed class GuidedSetupRulesTests
{
    [Theory]
    [InlineData(GuidedSetupCommand.Start, GuidedSetupStatus.NotStarted, GuidedSetupOutcome.Applied)]
    [InlineData(GuidedSetupCommand.Start, GuidedSetupStatus.InProgress, GuidedSetupOutcome.Unchanged)]
    [InlineData(GuidedSetupCommand.Start, GuidedSetupStatus.Completed, GuidedSetupOutcome.InvalidTransition)]
    [InlineData(GuidedSetupCommand.Start, GuidedSetupStatus.Skipped, GuidedSetupOutcome.InvalidTransition)]
    [InlineData(GuidedSetupCommand.SaveProgress, GuidedSetupStatus.NotStarted, GuidedSetupOutcome.InvalidTransition)]
    [InlineData(GuidedSetupCommand.SaveProgress, GuidedSetupStatus.InProgress, GuidedSetupOutcome.Applied)]
    [InlineData(GuidedSetupCommand.SaveProgress, GuidedSetupStatus.Completed, GuidedSetupOutcome.InvalidTransition)]
    [InlineData(GuidedSetupCommand.SaveProgress, GuidedSetupStatus.Skipped, GuidedSetupOutcome.InvalidTransition)]
    [InlineData(GuidedSetupCommand.Skip, GuidedSetupStatus.NotStarted, GuidedSetupOutcome.Applied)]
    [InlineData(GuidedSetupCommand.Skip, GuidedSetupStatus.InProgress, GuidedSetupOutcome.Applied)]
    [InlineData(GuidedSetupCommand.Skip, GuidedSetupStatus.Completed, GuidedSetupOutcome.InvalidTransition)]
    [InlineData(GuidedSetupCommand.Skip, GuidedSetupStatus.Skipped, GuidedSetupOutcome.Unchanged)]
    [InlineData(GuidedSetupCommand.Complete, GuidedSetupStatus.NotStarted, GuidedSetupOutcome.InvalidTransition)]
    [InlineData(GuidedSetupCommand.Complete, GuidedSetupStatus.InProgress, GuidedSetupOutcome.Applied)]
    [InlineData(GuidedSetupCommand.Complete, GuidedSetupStatus.Completed, GuidedSetupOutcome.Unchanged)]
    [InlineData(GuidedSetupCommand.Complete, GuidedSetupStatus.Skipped, GuidedSetupOutcome.InvalidTransition)]
    public void TransitionTable(GuidedSetupCommand command, GuidedSetupStatus current, GuidedSetupOutcome expected)
    {
        Assert.Equal(expected, GuidedSetupTransitions.Evaluate(command, current));
    }

    [Fact]
    public void OnlyCompletedAndSkipped_AreTerminal()
    {
        Assert.False(GuidedSetupTransitions.IsTerminal(GuidedSetupStatus.NotStarted));
        Assert.False(GuidedSetupTransitions.IsTerminal(GuidedSetupStatus.InProgress));
        Assert.True(GuidedSetupTransitions.IsTerminal(GuidedSetupStatus.Completed));
        Assert.True(GuidedSetupTransitions.IsTerminal(GuidedSetupStatus.Skipped));
    }

    [Fact]
    public void Plan_FollowsCapabilities_InJourneyOrder_WithoutUnauthorizedSections()
    {
        Assert.Equal(
            [GuidedSetupSections.PlatformConnections, GuidedSetupSections.PrintBridge, GuidedSetupSections.LiveScreenDemo],
            GuidedSetupPlan.SectionsFor(new GuidedSetupCapabilities(true, true, true)));

        // An order-management user without administrative permissions gets the operational journey only.
        Assert.Equal(
            [GuidedSetupSections.LiveScreenDemo],
            GuidedSetupPlan.SectionsFor(new GuidedSetupCapabilities(false, false, true)));

        Assert.Equal(
            [GuidedSetupSections.PlatformConnections, GuidedSetupSections.LiveScreenDemo],
            GuidedSetupPlan.SectionsFor(new GuidedSetupCapabilities(true, false, true)));

        // A read-only user has nothing to set up.
        Assert.Empty(GuidedSetupPlan.SectionsFor(new GuidedSetupCapabilities(false, false, false)));
    }

    [Fact]
    public void SectionKeys_AreStableSlugs_ThatFitTheColumn()
    {
        Assert.Equal(["platform-connections", "print-bridge", "live-screen-demo"], GuidedSetupSections.All);
        Assert.All(GuidedSetupSections.All, key =>
        {
            Assert.True(GuidedSetupSections.IsValidStepKey(key));
            Assert.True(key.Length <= GuidedSetupSections.MaxKeyLength);
        });
        Assert.False(GuidedSetupSections.IsKnown(null));
        Assert.False(GuidedSetupSections.IsKnown("print_bridge"));
        Assert.True(new GuidedSetupPosition(GuidedSetupSections.PrintBridge).IsValid);
        Assert.False(new GuidedSetupPosition(GuidedSetupSections.PrintBridge, "Install").IsValid);
    }
}
