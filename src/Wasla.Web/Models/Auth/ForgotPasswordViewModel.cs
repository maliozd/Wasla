using System.ComponentModel.DataAnnotations;

namespace Wasla.Web.Models.Auth;

public sealed class ForgotPasswordViewModel
{
    [Required(ErrorMessage = "Validation.EmailRequired")]
    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [Display(Name = "Auth.Email")]
    public string Email { get; set; } = string.Empty;

    public bool EmailSent { get; set; }
}
