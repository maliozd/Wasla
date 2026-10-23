namespace Wasla.Domain.Billing;

/// <summary>What a billing domain operation did. Only <see cref="Applied"/> changed the record.</summary>
public enum BillingChangeResult
{
    /// <summary>The record changed.</summary>
    Applied = 0,

    /// <summary>The record already held exactly this; a repeated command or callback.</summary>
    Unchanged = 1,

    /// <summary>The observation is not newer than the one the record holds.</summary>
    Stale = 2,

    /// <summary>The move is not allowed from the current state.</summary>
    InvalidTransition = 3,

    /// <summary>The input contradicts an immutable value or identifier the record already holds.</summary>
    Conflict = 4
}
