using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// The provider account a billing value was verified with. Every provider-driven write carries it, and it must match
/// the stored record, so a sandbox callback can never change a production subscription or the reverse.
/// </summary>
public readonly record struct BillingProviderScope(BillingProvider Provider, BillingEnvironment Environment);
