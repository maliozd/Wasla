namespace OrderHub.Web.Models.Admin;

public sealed class CentralAdminCustomerListItemViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string PrimaryDomain { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public DateTime? LastMigrationAt { get; set; }
    public string? LastMigrationResult { get; set; }
    public DateTime CreatedAt { get; set; }
}
