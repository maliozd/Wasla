namespace Wasla.Web.Models.Branches;

public sealed class BranchListViewModel
{
    public List<Row> Branches { get; set; } = new();

    public sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }
}

