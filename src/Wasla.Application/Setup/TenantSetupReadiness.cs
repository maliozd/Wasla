using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Domain.Enums;

namespace Wasla.Application.Setup;

/// <summary>
/// Derives tenant setup progress from persisted facts.
/// Print Bridge is optional and never blocks readiness.
/// Live Screen has no durable "opened" signal in the current model, so it stays an action
/// and does not block readiness. Do not mark it complete from in-memory browser state.
/// </summary>
public static class TenantSetupReadiness
{
    public static IReadOnlyList<FoodPlatform> SelectActivePlatforms(
        IEnumerable<PlatformConnectionFact> connections)
    {
        return connections
            .Where(IsUsablePlatform)
            .Select(connection => connection.Platform)
            .Distinct()
            .OrderBy(platform => platform)
            .ToArray();
    }

    public static TenantSetupStatus Evaluate(TenantSetupFacts facts, DateTime utcNow)
    {
        var restaurantComplete = facts.Restaurant.Available && IsRestaurantComplete(facts.Restaurant);
        var activePlatforms = facts.Platforms.Available
            ? SelectActivePlatforms(facts.Platforms.Connections)
            : Array.Empty<FoodPlatform>();
        var platformComplete = facts.Platforms.Available && activePlatforms.Count > 0;
        var notificationsComplete = facts.Notifications.Available
            && facts.Notifications.HasSavedSettings
            && facts.Notifications.SoundEnabled;
        var printingComplete = facts.Printing.Available && HasConnectedPrinter(facts.Printing.Devices, utcNow);

        var restaurant = new TenantSetupStep(
            TenantSetupStepKind.RestaurantProfile,
            restaurantComplete,
            IsOptional: false,
            CountsTowardReadiness: true,
            facts.Restaurant.Available);
        var platform = new TenantSetupStep(
            TenantSetupStepKind.PlatformConnection,
            platformComplete,
            IsOptional: false,
            CountsTowardReadiness: true,
            facts.Platforms.Available);
        var notifications = new TenantSetupStep(
            TenantSetupStepKind.Notifications,
            notificationsComplete,
            IsOptional: false,
            CountsTowardReadiness: true,
            facts.Notifications.Available);
        var printing = new TenantSetupStep(
            TenantSetupStepKind.Printing,
            printingComplete,
            IsOptional: true,
            CountsTowardReadiness: false,
            facts.Printing.Available);
        var liveScreen = new TenantSetupStep(
            TenantSetupStepKind.LiveScreen,
            IsComplete: false,
            IsOptional: false,
            CountsTowardReadiness: false,
            IsAvailable: true);

        var required = new[] { restaurant, platform, notifications };
        var requiredCompleted = required.Count(step => step.IsComplete);

        return new TenantSetupStatus(
            IsReady: required.All(step => step.IsAvailable && step.IsComplete),
            RequiredStepsCompleted: requiredCompleted,
            RequiredStepsTotal: required.Length,
            ActivePlatforms: activePlatforms,
            Restaurant: restaurant,
            Platform: platform,
            Notifications: notifications,
            Printing: printing,
            LiveScreen: liveScreen,
            IsSetupGuidanceCompleted: facts.SetupGuidanceCompleted,
            HasAdditionalTeamMembers: facts.Team is { Available: true }
                && HasAdditionalActiveTeamMember(facts.Team.Members));
    }

    /// <summary>
    /// The initial account is the earliest owner, or the earliest user when no owner row exists.
    /// Any other active user in the same tenant database counts as an additional team member.
    /// Inactive users do not count. Role is used only to identify the initial account.
    /// </summary>
    public static bool HasAdditionalActiveTeamMember(IEnumerable<TenantTeamMemberFact> members)
    {
        var list = members as IReadOnlyList<TenantTeamMemberFact> ?? members.ToArray();
        if (list.Count == 0)
            return false;

        var initial = list
            .Where(member => member.Role == UserRole.Owner)
            .OrderBy(member => member.CreatedAt)
            .ThenBy(member => member.Id)
            .FirstOrDefault();

        initial ??= list
            .OrderBy(member => member.CreatedAt)
            .ThenBy(member => member.Id)
            .First();

        return list.Any(member => member.IsActive && member.Id != initial.Id);
    }

    public static bool IsRestaurantComplete(RestaurantProfileFacts profile) =>
        !string.IsNullOrWhiteSpace(profile.BusinessName)
        && !string.IsNullOrWhiteSpace(profile.BusinessPhone)
        && (!string.IsNullOrWhiteSpace(profile.City) || !string.IsNullOrWhiteSpace(profile.Country));

    public static bool IsUsablePlatform(PlatformConnectionFact connection) =>
        connection.IsActive
        && !string.IsNullOrWhiteSpace(connection.StoreId)
        && connection.HasApiKey
        && connection.HasApiSecret;

    public static bool HasConnectedPrinter(IEnumerable<PrintingDeviceFact> devices, DateTime utcNow) =>
        devices.Any(device => IsConnected(device, utcNow));

    public static bool IsConnected(PrintingDeviceFact device, DateTime utcNow)
    {
        var status = PrintBridgeConnectionStatusCalculator.Calculate(
            device.IsActive,
            device.LastSeenAtUtc,
            utcNow);

        // Connected means a heartbeat within the existing 60-second window.
        // RecentlySeen, Disconnected, NeverConnected, and Inactive stay incomplete.
        return status == PrintBridgeConnectionStatus.Connected;
    }
}
