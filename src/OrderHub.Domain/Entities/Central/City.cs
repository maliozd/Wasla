namespace OrderHub.Domain.Entities.Central;

public class City
{
    public int Id { get; set; }

    public string CountryCode { get; set; } = default!;

    public string Name { get; set; } = default!;

    public string? PlateCode { get; set; }

    public string? PhoneAreaCode { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public ICollection<District> Districts { get; set; } = new List<District>();
}
