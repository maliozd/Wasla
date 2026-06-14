namespace Wasla.Application.Abstractions.Tenant;

public sealed record ResolvedTenantDto(
    Guid Id,
    string Name,
    string Slug,
    string PrimaryDomain);
