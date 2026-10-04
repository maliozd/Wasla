using Wasla.Domain.Enums;

namespace Wasla.Application.GuidedSetup;

public enum GuidedSetupCommand
{
    Start = 0,
    SaveProgress = 1,
    Skip = 2,
    Complete = 3
}

/// <summary>Machine-readable result of a guided-setup command.</summary>
public enum GuidedSetupOutcome
{
    /// <summary>The state changed.</summary>
    Applied = 0,

    /// <summary>The user is already in the requested state; nothing was written.</summary>
    Unchanged = 1,

    /// <summary>Not allowed from the current state; nothing was written.</summary>
    InvalidTransition = 2,

    /// <summary>The section or step key is not valid; nothing was written.</summary>
    InvalidPosition = 3
}

/// <summary>
/// The guided-setup state machine:
/// NotStarted → InProgress | Skipped, InProgress → InProgress (progress or pause) | Completed | Skipped.
/// Completed and Skipped are terminal; there is no reset or replay.
/// </summary>
public static class GuidedSetupTransitions
{
    public static GuidedSetupOutcome Evaluate(GuidedSetupCommand command, GuidedSetupStatus current) =>
        (command, current) switch
        {
            (GuidedSetupCommand.Start, GuidedSetupStatus.NotStarted) => GuidedSetupOutcome.Applied,
            (GuidedSetupCommand.Start, GuidedSetupStatus.InProgress) => GuidedSetupOutcome.Unchanged,
            (GuidedSetupCommand.SaveProgress, GuidedSetupStatus.InProgress) => GuidedSetupOutcome.Applied,
            (GuidedSetupCommand.Skip, GuidedSetupStatus.NotStarted) => GuidedSetupOutcome.Applied,
            (GuidedSetupCommand.Skip, GuidedSetupStatus.InProgress) => GuidedSetupOutcome.Applied,
            (GuidedSetupCommand.Skip, GuidedSetupStatus.Skipped) => GuidedSetupOutcome.Unchanged,
            (GuidedSetupCommand.Complete, GuidedSetupStatus.InProgress) => GuidedSetupOutcome.Applied,
            (GuidedSetupCommand.Complete, GuidedSetupStatus.Completed) => GuidedSetupOutcome.Unchanged,
            _ => GuidedSetupOutcome.InvalidTransition
        };

    public static bool IsTerminal(GuidedSetupStatus status) =>
        status is GuidedSetupStatus.Completed or GuidedSetupStatus.Skipped;
}
