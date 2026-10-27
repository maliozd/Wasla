using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Contact;

/// <summary>Contact messages for Central Admin, newest first. Read-only.</summary>
public interface ICentralAdminContactMessageService
{
    /// <summary>The page of messages (1-based), at most <see cref="AdminContactMessagePaging.MaxPageSize"/> per page.</summary>
    Task<AdminContactMessageListResult> GetListAsync(int page, int pageSize, CancellationToken ct);

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
    public string? Culture { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public ContactMessageNotificationStatus NotificationStatus { get; init; }
    public DateTime? NotificationAttemptedAtUtc { get; init; }
}
