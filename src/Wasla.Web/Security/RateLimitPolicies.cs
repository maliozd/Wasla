namespace Wasla.Web.Security;

/// <summary>Named rate-limiting policies registered in <c>Program.cs</c>.</summary>
public static class RateLimitPolicies
{
    /// <summary>Limits one-time setup code exchange attempts (automatic Print Bridge setup).</summary>
    public const string PrintBridgeSetupExchange = "print-bridge-setup-exchange";
}
