using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.PlatformConnections;

public sealed class PlatformConnectionListViewModel
{
    public List<Row> Connections { get; set; } = new();

    public sealed class Row
    {
        public Guid Id { get; set; }
        public FoodPlatform Platform { get; set; }
        public string StoreId { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime? CircuitOpenUntilUtc { get; set; }
        public DateTime? LastSuccessfulSyncUtc { get; set; }
    }
}

