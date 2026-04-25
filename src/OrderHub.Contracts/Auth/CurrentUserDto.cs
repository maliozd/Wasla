using OrderHub.Contracts.Enums;

namespace OrderHub.Contracts.Auth;

public record CurrentUserDto(
    Guid Id,
    string Email,
    string FullName,
    UserRoleDto Role);

