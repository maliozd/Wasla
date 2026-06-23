namespace Wasla.Application.Abstractions.Admin;

public interface ICentralAdminPendingRegistrationService
{
    Task<AdminPendingRegistrationCountsDto> GetCountsAsync(CancellationToken ct);

    Task<AdminPendingRegistrationListResult> GetListAsync(
        PendingRegistrationAdminFilter filter,
        CancellationToken ct);

    /// <summary>Returns registrations in PaymentSucceeded status that need immediate attention (not yet provisioned).</summary>
    Task<IReadOnlyList<AdminPendingRegistrationListItemDto>> GetAttentionListAsync(CancellationToken ct);

    Task<AdminPendingRegistrationDetailDto?> GetDetailAsync(Guid id, CancellationToken ct);
}
