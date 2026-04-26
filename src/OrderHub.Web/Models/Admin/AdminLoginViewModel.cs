using System.ComponentModel.DataAnnotations;

namespace OrderHub.Web.Models.Admin;

public sealed class AdminLoginViewModel
{
    [Required(ErrorMessage = "Validation.EmailRequired")]
    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [Display(Name = "Auth.Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.PasswordRequired")]
    [DataType(DataType.Password)]
    [Display(Name = "Auth.Password")]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}
