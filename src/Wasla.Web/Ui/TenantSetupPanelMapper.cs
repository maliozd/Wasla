using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Setup;
using Wasla.Domain.Enums;
using Wasla.Web.Models.Dashboard;
using SharedResource = Wasla.Web.SharedResource;

namespace Wasla.Web.Ui;

public static class TenantSetupPanelMapper
{
    public static TenantSetupPanelViewModel Map(
        TenantSetupStatus status,
        string restaurantName,
        Guid tenantId,
        IStringLocalizer<SharedResource> localizer)
    {
        var total = status.RequiredStepsTotal;
        var percent = total == 0
            ? 0
            : (int)Math.Round(100d * status.RequiredStepsCompleted / total, MidpointRounding.AwayFromZero);

        return new TenantSetupPanelViewModel
        {
            IsReady = status.IsReady,
            RequiredCompleted = status.RequiredStepsCompleted,
            RequiredTotal = total,
            ProgressPercent = percent,
            RestaurantName = restaurantName,
            TenantId = tenantId,
            ShowAddTeamMember = !status.HasAdditionalTeamMembers,
            Steps =
            [
                MapRestaurant(status.Restaurant, localizer),
                MapPlatform(status.Platform, status.ActivePlatforms, localizer),
                MapNotifications(status.Notifications, localizer),
                MapPrinting(status.Printing, localizer),
                MapLiveScreen(status.LiveScreen, localizer)
            ]
        };
    }

    private static TenantSetupStepViewModel MapRestaurant(
        TenantSetupStep step,
        IStringLocalizer<SharedResource> localizer) =>
        new()
        {
            Title = localizer["Setup.Restaurant.Title"].Value,
            Detail = Detail(step, localizer, localizer["Setup.Restaurant.Incomplete"].Value, completeDetail: null),
            IsComplete = step.IsComplete,
            IsOptional = step.IsOptional,
            IsUnavailable = !step.IsAvailable,
            ActionText = localizer["Setup.Restaurant.Action"].Value,
            ActionHref = "/settings/account",
            StatusText = StatusText(step, localizer)
        };

    private static TenantSetupStepViewModel MapPlatform(
        TenantSetupStep step,
        IReadOnlyList<FoodPlatform> activePlatforms,
        IStringLocalizer<SharedResource> localizer)
    {
        string? completeDetail = activePlatforms.Count switch
        {
            0 => null,
            1 => localizer["Setup.Platform.ConnectedOne", PlatformName(activePlatforms[0], localizer)].Value,
            _ => localizer["Setup.Platform.ConnectedMany", activePlatforms.Count].Value
        };

        return new TenantSetupStepViewModel
        {
            Title = localizer["Setup.Platform.Title"].Value,
            Detail = Detail(step, localizer, localizer["Setup.Platform.Incomplete"].Value, completeDetail),
            IsComplete = step.IsComplete,
            IsOptional = step.IsOptional,
            IsUnavailable = !step.IsAvailable,
            ActionText = localizer["Setup.Platform.Action"].Value,
            ActionHref = "/platform-connections",
            StatusText = StatusText(step, localizer)
        };
    }

    private static TenantSetupStepViewModel MapNotifications(
        TenantSetupStep step,
        IStringLocalizer<SharedResource> localizer) =>
        new()
        {
            Title = localizer["Setup.Notifications.Title"].Value,
            Detail = Detail(
                step,
                localizer,
                localizer["Setup.Notifications.Incomplete"].Value,
                localizer["Setup.Notifications.Complete"].Value),
            Hint = step.IsAvailable && !step.IsComplete
                ? localizer["Setup.Notifications.Hint"].Value
                : null,
            IsComplete = step.IsComplete,
            IsOptional = step.IsOptional,
            IsUnavailable = !step.IsAvailable,
            ActionText = localizer["Setup.Notifications.Action"].Value,
            ActionHref = "/settings/orders",
            StatusText = StatusText(step, localizer)
        };

    private static TenantSetupStepViewModel MapPrinting(
        TenantSetupStep step,
        IStringLocalizer<SharedResource> localizer) =>
        new()
        {
            Title = step.IsComplete
                ? localizer["Setup.Printing.Complete"].Value
                : localizer["Setup.Printing.Title"].Value,
            Detail = !step.IsAvailable ? localizer["Setup.StatusUnavailable"].Value : null,
            IsComplete = step.IsComplete,
            IsOptional = true,
            IsUnavailable = !step.IsAvailable,
            ActionText = localizer["Setup.Printing.Action"].Value,
            ActionHref = "/print-bridge/setup",
            StatusText = StatusText(step, localizer)
        };

    private static TenantSetupStepViewModel MapLiveScreen(
        TenantSetupStep _,
        IStringLocalizer<SharedResource> localizer) =>
        new()
        {
            Title = localizer["Setup.LiveScreen.Title"].Value,
            Detail = localizer["Setup.LiveScreen.Detail"].Value,
            IsComplete = false,
            IsOptional = false,
            IsUnavailable = false,
            ShowsCompletionMark = false,
            ActionText = localizer["Setup.LiveScreen.Action"].Value,
            ActionHref = "/orders/live-display",
            OpenInNewTab = true,
            StatusText = localizer["Setup.LiveScreen.Status"].Value
        };

    private static string? Detail(
        TenantSetupStep step,
        IStringLocalizer<SharedResource> localizer,
        string incompleteDetail,
        string? completeDetail)
    {
        if (!step.IsAvailable)
            return localizer["Setup.StatusUnavailable"].Value;

        return step.IsComplete ? completeDetail : incompleteDetail;
    }

    private static string StatusText(TenantSetupStep step, IStringLocalizer<SharedResource> localizer)
    {
        if (!step.IsAvailable)
            return localizer["Setup.StatusUnavailable"].Value;

        return step.IsComplete
            ? localizer["Setup.StatusComplete"].Value
            : localizer["Setup.StatusIncomplete"].Value;
    }

    private static string PlatformName(FoodPlatform platform, IStringLocalizer<SharedResource> localizer) =>
        platform switch
        {
            FoodPlatform.Yemeksepeti => localizer["Orders.PlatformYemeksepeti"].Value,
            FoodPlatform.GetirYemek => localizer["Orders.PlatformGetirYemek"].Value,
            FoodPlatform.TrendyolYemek => localizer["Orders.PlatformTrendyolYemek"].Value,
            _ => localizer["Setup.Platform.Title"].Value
        };
}
