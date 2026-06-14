namespace Wasla.Domain.Entities.Central;

public class BusinessType
{
    public int Id { get; set; }

    public string Code { get; set; } = default!;

    public string DisplayName { get; set; } = default!;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }
}
