namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// A provider-hosted page (checkout or card update). The adapter sets exactly one of <see cref="RedirectUrl"/> and
/// <see cref="EmbeddedFormContent"/>, depending on what the provider returns; WAS-149 decides how Web presents it.
/// <see cref="SessionToken"/> identifies the session when the provider calls back and is not a payment result.
/// </summary>
public sealed record BillingHostedSession(
    string SessionToken,
    Uri? RedirectUrl,
    string? EmbeddedFormContent,
    DateTime? ExpiresAtUtc);
