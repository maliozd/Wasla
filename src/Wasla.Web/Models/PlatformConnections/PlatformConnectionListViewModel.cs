using Wasla.Domain.Enums;

namespace Wasla.Web.Models.PlatformConnections;

public sealed class PlatformConnectionListViewModel
{
    public List<Row> Connections { get; set; } = new();

    /// <summary>Set only while Platform Connections is the user's current guided-setup section.</summary>
    public Wasla.Web.Models.GuidedSetup.GuidedSetupSectionPanelViewModel? GuidedSetup { get; set; }

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

