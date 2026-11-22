namespace Wasla.Domain.Enums;

/// <summary>
/// Where the Wasla team stands with a pilot application (WAS-222). Every change of status, and every contact the team
/// records, moves the application's last meaningful activity, from which its retention period runs.
/// </summary>
public enum PilotApplicationStatus
{
    /// <summary>Received; the team has not answered yet.</summary>
    New = 0,

    /// <summary>The team is in contact with the restaurant.</summary>
    Contacted = 1,

    /// <summary>Accepted into the pilot; the restaurant continues in the separately governed onboarding process.</summary>
    Accepted = 2,

    /// <summary>Not taken into the pilot.</summary>
    Declined = 3
}
