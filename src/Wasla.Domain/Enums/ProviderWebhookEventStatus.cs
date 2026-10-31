namespace Wasla.Domain.Enums;

/// <summary>Where a stored provider webhook event is in its processing.</summary>
public enum ProviderWebhookEventStatus
{
    /// <summary>Waiting to be processed, now or at its next attempt time.</summary>
    Pending = 0,

    /// <summary>Claimed by a processor until its lock expires.</summary>
    Processing = 1,

    /// <summary>Processed; <c>Outcome</c> says what was done.</summary>
    Processed = 2,

    /// <summary>Stored but not processed by design (an event type Wasla does not act on).</summary>
    Ignored = 3,

    /// <summary>Contradicts the configured seller, store or connection: never processed.</summary>
    Rejected = 4,

    /// <summary>Processing failed on every attempt; polling still reconciles the order. Can be replayed.</summary>
    Failed = 5
}
