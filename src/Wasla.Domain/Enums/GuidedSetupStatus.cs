namespace Wasla.Domain.Enums;

/// <summary>
/// One tenant user's guided-setup choice. Completed and Skipped are terminal.
/// NotStarted is never stored: a user without a row has not started.
/// </summary>
public enum GuidedSetupStatus
{
    NotStarted = 0,
    InProgress = 1,
    Completed = 2,
    Skipped = 3
}
