using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Auth;

public sealed record AuthSessionResult(
    Guid CustomerId,
    Guid UserId,
    string Email,
    string FullName,
    UserRole Role);

