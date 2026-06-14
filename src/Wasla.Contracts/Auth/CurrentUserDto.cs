using Wasla.Contracts.Enums;

namespace Wasla.Contracts.Auth;

public record CurrentUserDto(
    Guid Id,
    string Email,
    string FullName,
    UserRoleDto Role);

