using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Wasla.Web.Areas.Admin.Models;

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

    [Display(Name = "Auth.RememberMe")]
    [ValidateNever]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

