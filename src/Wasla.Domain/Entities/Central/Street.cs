namespace Wasla.Domain.Entities.Central;

public class Street
{
    public int Id { get; set; }

    public int NeighborhoodId { get; set; }

    public string Name { get; set; } = default!;

    public string? StreetType { get; set; }

    public string? ExternalCode { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public Neighborhood Neighborhood { get; set; } = default!;
}
