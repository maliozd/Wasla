using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Auth;

public sealed record AuthSessionResult(
    Guid TenantId,
    Guid UserId,
    string Email,
    string FullName,
    UserRole Role,
    Guid SecurityStamp);
