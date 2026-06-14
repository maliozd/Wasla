namespace OrderHub.Domain.Entities.Central;

public class Neighborhood
{
    public int Id { get; set; }

    public int DistrictId { get; set; }

    public string Name { get; set; } = default!;

    public string? ExternalCode { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public District District { get; set; } = default!;

    public ICollection<Street> Streets { get; set; } = new List<Street>();
}
