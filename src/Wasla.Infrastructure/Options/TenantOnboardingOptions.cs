namespace Wasla.Infrastructure.Options;

public sealed class TenantOnboardingOptions
{
    public const string SectionName = "Wasla:TenantOnboarding";

    public string MarketingBaseDomain { get; set; } = "wasla.local";

    /// <summary>trusted or sql:username:password</summary>
    public string SqlAuth { get; set; } = "trusted";

    public int TrialDays { get; set; } = 14;

    /// <summary>How long a pending registration remains valid before expiry.</summary>
    public int PendingRegistrationExpiryDays { get; set; } = 7;

    /// <summary>
    /// How many active registrations (Draft, AwaitingPayment or PaymentSucceeded, not expired) one owner email may have.
    /// A further signup with that email is refused until one is completed, cancelled or expires. Values below 1 count as 1.
    /// </summary>
    public int MaxActivePendingRegistrationsPerOwnerEmail { get; set; } = 3;
}
