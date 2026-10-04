using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Setup;

public enum TenantSetupStepKind
{
    RestaurantProfile = 1,
    PlatformConnection = 2,
    Notifications = 3,
    Printing = 4,
    LiveScreen = 5
}

/// <summary>
/// Inputs already stored by Wasla. Callers must not contact providers or devices to fill this.
/// </summary>
public sealed record TenantSetupFacts(
    RestaurantProfileFacts Restaurant,
    PlatformConnectionFacts Platforms,
    NotificationSetupFacts Notifications,
    PrintingSetupFacts Printing,
    bool SetupGuidanceCompleted = false,
    TeamMembershipFacts? Team = null);

public sealed record RestaurantProfileFacts(
    bool Available,
    string? BusinessName,
    string? BusinessPhone,
    string? City,
    string? Country);

public sealed record PlatformConnectionFacts(
    bool Available,
    IReadOnlyList<PlatformConnectionFact> Connections);

public sealed record PlatformConnectionFact(
    FoodPlatform Platform,
    bool IsActive,
    string? StoreId,
    bool HasApiKey,
    bool HasApiSecret);

public sealed record NotificationSetupFacts(
    bool Available,
    bool HasSavedSettings,
    bool SoundEnabled);

public sealed record PrintingSetupFacts(
    bool Available,
    IReadOnlyList<PrintingDeviceFact> Devices);

public sealed record PrintingDeviceFact(
    bool IsActive,
    DateTime? LastSeenAtUtc);

public sealed record TeamMembershipFacts(
    bool Available,
    IReadOnlyList<TenantTeamMemberFact> Members);

public sealed record TenantTeamMemberFact(
    Guid Id,
    UserRole Role,
    bool IsActive,
    DateTime CreatedAt);

public sealed record TenantSetupStep(
    TenantSetupStepKind Kind,
    bool IsComplete,
    bool IsOptional,
    bool CountsTowardReadiness,
    bool IsAvailable);

public sealed record TenantSetupStatus(
    bool IsReady,
    int RequiredStepsCompleted,
    int RequiredStepsTotal,
    IReadOnlyList<FoodPlatform> ActivePlatforms,
    TenantSetupStep Restaurant,
    TenantSetupStep Platform,
    TenantSetupStep Notifications,
    TenantSetupStep Printing,
    TenantSetupStep LiveScreen,
    bool IsSetupGuidanceCompleted = false,
    bool HasAdditionalTeamMembers = false);
