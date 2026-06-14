namespace Wasla.Web.Models.Checkout;

public sealed class CheckoutResultViewModel
{
    public Guid RegistrationId { get; set; }

    public string? BusinessName { get; set; }

    public string? PrimaryDomain { get; set; }
}
