namespace OrderHub.Application.Abstractions.Admin;

public sealed class CentralAdminDashboardResult
{
    public int TotalCustomers { get; init; }
    public int ActiveCustomers { get; init; }
    public int InactiveCustomers { get; init; }
    public IReadOnlyList<CentralAdminCustomerListItemDto> Customers { get; init; } =
        Array.Empty<CentralAdminCustomerListItemDto>();
}

public sealed class CentralAdminCustomerListItemDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string PrimaryDomain { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string SchemaVersion { get; init; } = string.Empty;
    public DateTime? LastMigrationAt { get; init; }
    public string? LastMigrationResult { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Safe read model for a single customer (no connection string fields).
/// </summary>
public sealed class CentralAdminCustomerDetailResult
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string PrimaryDomain { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string SchemaVersion { get; init; } = string.Empty;
    public DateTime? LastMigrationAt { get; init; }
    public string? LastMigrationResult { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
