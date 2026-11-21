using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Contact;

/// <summary>Which stored messages a Central Admin list shows.</summary>
public enum AdminContactMessageFilter
{
    All,

    /// <summary>Pilot restaurant applications from <c>/pilot</c> only (WAS-217).</summary>
    PilotApplications,

    /// <summary>Contact form messages only.</summary>
    ContactMessages,

    /// <summary>
    /// Messages of either kind whose team notification failed, was not configured, or started more than
    /// <see cref="Wasla.Application.Contact.ContactNotificationRetry.StuckAfter"/> ago without an outcome: the ones Central
    /// Admin may send again (<see cref="Wasla.Application.Contact.ContactNotificationRetry.NeedsAttentionAt"/>).
    /// </summary>
    NotificationNeedsAttention
}

/// <summary>Contact messages and pilot applications for Central Admin, newest first.</summary>
public interface ICentralAdminContactMessageService
{
    /// <summary>The page of messages (1-based), at most <see cref="AdminContactMessagePaging.MaxPageSize"/> per page.</summary>
    Task<AdminContactMessageListResult> GetListAsync(int page, int pageSize, AdminContactMessageFilter filter, CancellationToken ct);

    Task<AdminContactMessageDetailDto?> GetDetailAsync(Guid id, CancellationToken ct);
}

public static class AdminContactMessagePaging
{
    public const int MaxPageSize = 50;

    public static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);
}

public sealed class AdminContactMessageListResult
{
    public IReadOnlyList<AdminContactMessageListItemDto> Items { get; init; } = [];

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages => TotalCount == 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;
}

public sealed class AdminContactMessageListItemDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string RestaurantName { get; init; } = string.Empty;
    public ContactTopic Topic { get; init; }
    public ContactMessageNotificationStatus NotificationStatus { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class AdminContactMessageDetailDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string RestaurantName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public ContactTopic Topic { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>The plate code of a pilot application's province, or null.</summary>
    public string? CityPlateCode { get; init; }

    /// <summary>The province's name from the city catalog, or null when the catalog has no active province for the code.</summary>
    public string? CityName { get; init; }

    public string? Culture { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public ContactMessageNotificationStatus NotificationStatus { get; init; }
    public DateTime? NotificationAttemptedAtUtc { get; init; }

    /// <summary>Whether Central Admin may send the team notification again now (see <see cref="IContactMessageService.ResendNotificationAsync"/>).</summary>
    public bool CanResendNotification { get; init; }
}
