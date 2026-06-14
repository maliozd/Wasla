namespace OrderHub.Domain.Entities.Central;

public class Country
{
    public int Id { get; set; }

    public string Code { get; set; } = default!;

    public string Name { get; set; } = default!;

    public bool IsActive { get; set; }
}
