namespace OrderHub.Application.Abstractions.Tenant;

public sealed record ResolvedCustomerDto(
    Guid Id,
    string Name,
    string Slug,
    string PrimaryDomain);

