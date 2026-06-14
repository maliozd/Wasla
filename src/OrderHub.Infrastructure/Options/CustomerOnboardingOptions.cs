namespace OrderHub.Infrastructure.Options;

public sealed class CustomerOnboardingOptions
{
    public const string SectionName = "OrderHub:CustomerOnboarding";

    public string MarketingBaseDomain { get; set; } = "orderhub.local";

    public string ServerInstance { get; set; } = "(localdb)\\MSSQLLocalDB";

    /// <summary>trusted or sql:username:password</summary>
    public string SqlAuth { get; set; } = "trusted";

    public int TrialDays { get; set; } = 14;
}
