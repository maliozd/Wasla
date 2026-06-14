namespace OrderHub.Domain.Entities.Central;

public class District
{
    public int Id { get; set; }

    public int CityId { get; set; }

    public string Name { get; set; } = default!;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public City City { get; set; } = default!;

    public ICollection<Neighborhood> Neighborhoods { get; set; } = new List<Neighborhood>();
}
