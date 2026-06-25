using System.ComponentModel.DataAnnotations;

namespace Wasla.Web.Models.Auth;

public sealed class ResetPasswordViewModel
{
    [Required(ErrorMessage = "Auth.ResetPassword.InvalidOrExpired")]
    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.PasswordRequired")]
    [MinLength(8, ErrorMessage = "Validation.PasswordMinLength")]
    [DataType(DataType.Password)]
    [Display(Name = "Auth.ResetPassword.NewPassword")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.ConfirmPasswordRequired")]
    [Compare(nameof(NewPassword), ErrorMessage = "Validation.PasswordMismatch")]
    [DataType(DataType.Password)]
    [Display(Name = "Auth.ResetPassword.ConfirmNewPassword")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public bool IsInvalidToken { get; set; }
}
