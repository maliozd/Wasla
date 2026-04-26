using System.ComponentModel.DataAnnotations;

namespace OrderHub.Web.Models.Auth;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Validation.EmailRequired")]
    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [Display(Name = "Auth.Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.PasswordRequired")]
    [Display(Name = "Auth.Password")]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}

