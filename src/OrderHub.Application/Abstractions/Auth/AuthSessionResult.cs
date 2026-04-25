using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Auth;

public sealed record AuthSessionResult(
    Guid CustomerId,
    Guid UserId,
    string Email,
    string FullName,
    UserRole Role);

