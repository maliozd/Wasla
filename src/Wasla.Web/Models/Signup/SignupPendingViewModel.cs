namespace Wasla.Web.Models.Signup;

public sealed class SignupPendingViewModel
{
    public Guid RegistrationId { get; set; }

    public string PrimaryDomain { get; set; } = string.Empty;

    public string BusinessName { get; set; } = string.Empty;

    public string PlanCode { get; set; } = string.Empty;

    public string PlanDisplayName { get; set; } = string.Empty;

    public string BillingPeriod { get; set; } = string.Empty;
}
