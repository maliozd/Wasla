namespace OrderHub.Web.Models.Checkout;

public sealed class CheckoutResultViewModel
{
    public Guid RegistrationId { get; set; }

    public string? BusinessName { get; set; }

    public string? PrimaryDomain { get; set; }

    public string? SimulatedPaymentReference { get; set; }

    public bool IsInformationalOnly { get; set; }
}
