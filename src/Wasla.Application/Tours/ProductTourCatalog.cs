using Wasla.Application.Orders;
using Wasla.Domain.Enums;

namespace Wasla.Application.Tours;

public static class ProductTourKeys
{
    public const string DashboardIntro = "dashboard-intro:v1";
    public const string LiveScreenIntro = "live-screen-intro:v1";
    public const string PlatformConnectionsIntro = "platform-connections-intro:v1";
    public const string GuidedDemo = "guided-demo-order:v1";

    public static bool IsStorable(string? tourKey) =>
        tourKey is not null
        && System.Text.RegularExpressions.Regex.IsMatch(
            tourKey,
            "^[a-z0-9-]{1,48}:v[1-9][0-9]{0,2}$");
}

public sealed record ProductTourStepDefinition(
    string Target,
    string Placement,
    string TitleKey,
    string BodyKey,
    string? Advance = null,
    string? EventName = null,
    string? EventAction = null,
    string? ReadySelector = null,
    string? PrimaryAction = null,
    string? PrimaryLabelKey = null,
    string? CompleteSelector = null,
    string? ResumeSelector = null,
    object[]? BodyArguments = null);

/// <summary>
/// Which tour steps a role may see. Missing targets are removed later by the client.
/// </summary>
public static class ProductTourCatalog
{
    public static IReadOnlyList<ProductTourStepDefinition> StepsFor(string tourKey, UserRole role)
    {
        if (tourKey == ProductTourKeys.DashboardIntro
            && role is UserRole.Owner or UserRole.Manager or UserRole.Viewer)
        {
            return
            [
                new("dashboard-summary", "bottom", "Tour.Dashboard.Summary.Title", "Tour.Dashboard.Summary.Body"),
                new("dashboard-recent", "top", "Tour.Dashboard.Recent.Title", "Tour.Dashboard.Recent.Body")
            ];
        }

        if (tourKey == ProductTourKeys.LiveScreenIntro
            && role is UserRole.Owner or UserRole.Manager or UserRole.Kitchen or UserRole.Cashier or UserRole.Viewer)
        {
            var steps = new List<ProductTourStepDefinition>
            {
                new("live-orders", "bottom", "Tour.Live.Orders.Title", "Tour.Live.Orders.Body")
            };

            if (role != UserRole.Viewer)
            {
                steps.Add(new("live-order-actions", "top", "Tour.Live.Actions.Title", "Tour.Live.Actions.Body"));
            }

            steps.Add(new("live-timer", "bottom", "Tour.Live.Timer.Title", "Tour.Live.Timer.Body"));
            steps.Add(new("live-view-selector", "bottom", "Tour.Live.Views.Title", "Tour.Live.Views.Body"));
            return steps;
        }

        if (tourKey == ProductTourKeys.PlatformConnectionsIntro && role == UserRole.Owner)
        {
            return
            [
                new("platform-list", "bottom", "Tour.Platform.List.Title", "Tour.Platform.List.Body"),
                new("platform-connect", "bottom", "Tour.Platform.Connect.Title", "Tour.Platform.Connect.Body")
            ];
        }

        if (tourKey == ProductTourKeys.GuidedDemo
            && role is UserRole.Owner or UserRole.Manager or UserRole.Kitchen or UserRole.Cashier)
        {
            return
            [
                new("demo-order-start", "bottom", "Demo.StartTitle", "Demo.StartBody",
                    "event", "wasla:demo-order-started", null, "[data-tour='demo-order-start']",
                    "demo-start", "Demo.LaunchAction"),
                new("demo-order-actions", "top", "Demo.ApproveTitle", "Demo.ApproveBody",
                    "event", "wasla:order-action-completed", "approve", "[data-order-action='approve'][data-wasla-demo]"),
                new("demo-order-actions", "top", "Demo.PrepareTitle", "Demo.PrepareBody",
                    "event", "wasla:order-action-completed", "start-preparing", "[data-order-action='start-preparing'][data-wasla-demo]"),
                new("demo-order-actions", "top", "Demo.ReadyTitle", "Demo.ReadyBody",
                    "event", "wasla:order-action-completed", "mark-ready", "[data-order-action='mark-ready'][data-wasla-demo]"),
                // Mark ready is the restaurant's last action. The next two steps are passive: the Worker
                // plays the platform courier, and each step advances when the snapshot shows the new status.
                new("demo-order-card", "top", "Demo.PickupWaitTitle", "Demo.PickupWaitBody",
                    Advance: "state",
                    ReadySelector: "[data-wasla-demo][data-order-status='ReadyForPickup']",
                    CompleteSelector: "[data-wasla-demo][data-order-status='OnTheWay']"),
                new("demo-order-card", "top", "Demo.DeliveryWaitTitle", "Demo.DeliveryWaitBody",
                    Advance: "state",
                    ReadySelector: "[data-wasla-demo][data-order-status='OnTheWay']",
                    CompleteSelector: "[data-wasla-demo][data-order-status='Delivered']"),
                new("live-view-selector", "bottom", "Demo.SuccessTitle", "Demo.SuccessBody",
                    PrimaryAction: "demo-finish", PrimaryLabelKey: "Demo.ContinueLive",
                    ResumeSelector: "[data-wasla-demo][data-order-status='Delivered']",
                    BodyArguments: [(int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes])
            ];
        }

        return [];
    }
}
