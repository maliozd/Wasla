using System.ComponentModel.DataAnnotations;
using Wasla.Domain.Enums;

namespace Wasla.Web.Models.TenantUsers;

public sealed class TenantUsersViewModel
{
    public IReadOnlyList<TenantUserRowViewModel> Users { get; init; } = [];

    public IReadOnlyList<UserRole> RoleOptions { get; init; } = TenantUserRoleOptions.All;

    public int TotalCount { get; init; }

    public int ActiveCount { get; init; }

    public IReadOnlyList<TenantUserRoleCountViewModel> RoleCounts { get; init; } = [];
}

public sealed class TenantUserRoleCountViewModel
{
    public UserRole Role { get; init; }

    public int Count { get; init; }
}

public class TenantUserRowViewModel
{
    public Guid Id { get; init; }

    public string Email { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public UserRole Role { get; init; }

    public bool IsActive { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? LastLoginAt { get; init; }
}

public sealed class TenantUserDetailsViewModel : TenantUserRowViewModel
{
}

public sealed class TenantUserCreateViewModel
{
    [Required(ErrorMessage = "Validation.EmailRequired")]
    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [Display(Name = "TenantUsers.Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "TenantUsers.NameRequired")]
    [Display(Name = "TenantUsers.Name")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "TenantUsers.InvalidRole")]
    [Display(Name = "TenantUsers.Role")]
    public string Role { get; set; } = UserRole.Viewer.ToString();

    [Display(Name = "TenantUsers.Active")]
    public bool IsActive { get; set; } = true;

    [Required(ErrorMessage = "Validation.PasswordRequired")]
    [DataType(DataType.Password)]
    [Display(Name = "TenantUsers.Password")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.ConfirmPasswordRequired")]
    [Compare(nameof(Password), ErrorMessage = "Validation.PasswordMismatch")]
    [DataType(DataType.Password)]
    [Display(Name = "TenantUsers.ConfirmPassword")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public IReadOnlyList<UserRole> RoleOptions { get; init; } = TenantUserRoleOptions.All;
}

public sealed class TenantUserEditViewModel
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "TenantUsers.NameRequired")]
    [Display(Name = "TenantUsers.Name")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "TenantUsers.InvalidRole")]
    [Display(Name = "TenantUsers.Role")]
    public string Role { get; set; } = UserRole.Viewer.ToString();

    [Display(Name = "TenantUsers.Active")]
    public bool IsActive { get; set; } = true;

    [DataType(DataType.Password)]
    [Display(Name = "TenantUsers.Password")]
    public string? NewPassword { get; set; }

    [Compare(nameof(NewPassword), ErrorMessage = "Validation.PasswordMismatch")]
    [DataType(DataType.Password)]
    [Display(Name = "TenantUsers.ConfirmPassword")]
    public string? ConfirmPassword { get; set; }

    public IReadOnlyList<UserRole> RoleOptions { get; init; } = TenantUserRoleOptions.All;
}

public static class TenantUserRoleOptions
{
    public static readonly IReadOnlyList<UserRole> All =
    [
        UserRole.Owner,
        UserRole.Manager,
        UserRole.Kitchen,
        UserRole.Cashier,
        UserRole.Viewer
    ];
}
