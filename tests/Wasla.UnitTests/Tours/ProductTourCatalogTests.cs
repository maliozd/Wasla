using Wasla.Application.Orders;
using Wasla.Application.Tours;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Tours;

public sealed class ProductTourCatalogTests
{
    [Theory]
    [InlineData(UserRole.Owner)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    public void GuidedDemo_TeachesRestaurantActions_ThenWaitsForThePlatformCourier(UserRole role)
    {
        var steps = ProductTourCatalog.StepsFor(ProductTourKeys.GuidedDemo, role);
        Assert.Equal(7, steps.Count);

        // Mark ready is the restaurant's last action.
        Assert.Equal(new[] { "approve", "start-preparing", "mark-ready" },
            steps.Where(step => step.EventAction is not null).Select(step => step.EventAction));
        Assert.All(steps.Skip(1).Take(3), step =>
        {
            Assert.Equal("event", step.Advance);
            Assert.Contains("[data-wasla-demo]", step.ReadySelector);
            Assert.Null(step.PrimaryAction);
        });
        Assert.DoesNotContain(steps, step =>
            (step.ReadySelector ?? string.Empty).Contains("hand-to-courier")
            || (step.ReadySelector ?? string.Empty).Contains("mark-delivered"));

        // The platform courier's pickup and delivery are passive waits that advance on the snapshot.
        var pickup = steps[4];
        Assert.Equal(("state", "Demo.PickupWaitTitle"), (pickup.Advance, pickup.TitleKey));
        Assert.Contains("data-order-status='ReadyForPickup'", pickup.ReadySelector);
        Assert.Contains("data-order-status='OnTheWay'", pickup.CompleteSelector);
        var delivery = steps[5];
        Assert.Equal(("state", "Demo.DeliveryWaitTitle"), (delivery.Advance, delivery.TitleKey));
        Assert.Contains("data-order-status='OnTheWay'", delivery.ReadySelector);
        Assert.Contains("data-order-status='Delivered'", delivery.CompleteSelector);
        Assert.All(new[] { pickup, delivery }, step =>
        {
            Assert.Null(step.EventAction);
            Assert.Null(step.PrimaryAction);
        });

        // Only the final success step leads into the normal Live Screen.
        Assert.Equal(new[] { "Demo.ContinueLive" },
            steps.Where(step => step.PrimaryAction == "demo-finish").Select(step => step.PrimaryLabelKey));
        Assert.Equal("demo-finish", steps[^1].PrimaryAction);
        Assert.Equal("Demo.SuccessTitle", steps[^1].TitleKey);
        Assert.Contains("data-order-status='Delivered'", steps[^1].ResumeSelector);
    }

    [Fact]
    public void SuccessCopy_UsesTheCanonicalLiveScreenDeliveredWindow()
    {
        var success = ProductTourCatalog.StepsFor(ProductTourKeys.GuidedDemo, UserRole.Owner)[^1];

        Assert.Equal(2, LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes);
        Assert.Equal(new object[] { (int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes }, success.BodyArguments);
    }

    [Fact]
    public void Dashboard_IsAvailableToReportingRolesOnly()
    {
        Assert.Equal(2, ProductTourCatalog.StepsFor(ProductTourKeys.DashboardIntro, UserRole.Owner).Count);
        Assert.Equal(2, ProductTourCatalog.StepsFor(ProductTourKeys.DashboardIntro, UserRole.Manager).Count);
        Assert.Equal(2, ProductTourCatalog.StepsFor(ProductTourKeys.DashboardIntro, UserRole.Viewer).Count);
        Assert.Empty(ProductTourCatalog.StepsFor(ProductTourKeys.DashboardIntro, UserRole.Kitchen));
        Assert.Empty(ProductTourCatalog.StepsFor(ProductTourKeys.DashboardIntro, UserRole.Cashier));
    }

    [Fact]
    public void LiveScreen_OmitsOrderActionsForViewer()
    {
        var owner = ProductTourCatalog.StepsFor(ProductTourKeys.LiveScreenIntro, UserRole.Owner);
        var viewer = ProductTourCatalog.StepsFor(ProductTourKeys.LiveScreenIntro, UserRole.Viewer);
        var kitchen = ProductTourCatalog.StepsFor(ProductTourKeys.LiveScreenIntro, UserRole.Kitchen);

        Assert.Contains(owner, step => step.Target == "live-order-actions");
        Assert.Contains(kitchen, step => step.Target == "live-order-actions");
        Assert.DoesNotContain(viewer, step => step.Target == "live-order-actions");
        Assert.Contains(viewer, step => step.Target == "live-orders");
        Assert.Contains(viewer, step => step.Target == "live-view-selector");
    }

    [Fact]
    public void PlatformConnections_AreOwnerOnly()
    {
        Assert.Equal(2, ProductTourCatalog.StepsFor(ProductTourKeys.PlatformConnectionsIntro, UserRole.Owner).Count);
        Assert.Empty(ProductTourCatalog.StepsFor(ProductTourKeys.PlatformConnectionsIntro, UserRole.Manager));
        Assert.Empty(ProductTourCatalog.StepsFor(ProductTourKeys.PlatformConnectionsIntro, UserRole.Kitchen));
        Assert.Empty(ProductTourCatalog.StepsFor(ProductTourKeys.PlatformConnectionsIntro, UserRole.Viewer));
    }

    [Fact]
    public void VersionedKeys_AreStorableWithoutBeingTheSameTour()
    {
        Assert.True(ProductTourKeys.IsStorable(ProductTourKeys.LiveScreenIntro));
        Assert.True(ProductTourKeys.IsStorable("live-screen-intro:v2"));
        Assert.NotEqual(ProductTourKeys.LiveScreenIntro, "live-screen-intro:v2");
        Assert.False(ProductTourKeys.IsStorable("live-screen-intro"));
        Assert.False(ProductTourKeys.IsStorable("../secret"));
    }
}
