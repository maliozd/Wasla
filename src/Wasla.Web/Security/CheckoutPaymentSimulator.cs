namespace Wasla.Web.Security;

/// <summary>
/// Where the checkout payment simulator exists. It is a local-development tool: anywhere else it would let a
/// request mark a registration as paid, which makes it eligible for provisioning.
/// </summary>
/// <remarks>
/// The simulator endpoints and every page that mentions a simulated payment use this one check, so the copy
/// never promises a simulator the server refuses. It reads the host environment, never a request value.
/// </remarks>
public static class CheckoutPaymentSimulator
{
    public static bool IsAvailable(IHostEnvironment environment) => environment.IsDevelopment();
}
