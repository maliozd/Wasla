using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wasla.Application.PlatformConnections;
using Wasla.Domain.Enums;

namespace Wasla.Web.Models.PlatformConnections;

public sealed class EditPlatformConnectionViewModel
{
    public Guid Id { get; set; }

    [Display(Name = "PlatformConnections.Platform")]
    public FoodPlatform Platform { get; set; }

    [Required(ErrorMessage = "Validation.StoreIdRequired")]
    [Display(Name = "PlatformConnections.StoreId")]
    public string StoreId { get; set; } = string.Empty;

    [Display(Name = "Common.Active")]
    public bool IsActive { get; set; } = true;

    // A replacement typed into this form only: stored credentials are never put here, and a failed submission clears
    // it before the form is shown again. Empty keeps the stored value.
    [Display(Name = "PlatformConnections.ApiKey")]
    [StringLength(CreatePlatformConnectionCommandValidator.MaxCredentialLength, ErrorMessage = "Validation.ApiKeyMaxLength")]
    public string? ApiKey { get; set; }

    [Display(Name = "PlatformConnections.ApiSecret")]
    [StringLength(CreatePlatformConnectionCommandValidator.MaxCredentialLength, ErrorMessage = "Validation.ApiSecretMaxLength")]
    public string? ApiSecret { get; set; }

    /// <summary>Whether an API key is stored. Only presence: never any part of the value.</summary>
    [BindNever]
    public bool HasStoredApiKey { get; set; }

    /// <summary>Whether an API secret is stored. Only presence: never any part of the value.</summary>
    [BindNever]
    public bool HasStoredApiSecret { get; set; }

    /// <summary>The submission failed after credentials were typed; they were cleared and must be entered again.</summary>
    [BindNever]
    public bool CredentialsNeedReentry { get; set; }

    [Display(Name = "PlatformConnections.SupplierId")]
    public string? SupplierId { get; set; }

    [Display(Name = "PlatformConnections.ExecutorEmail")]
    [EmailAddress(ErrorMessage = "Validation.ExecutorEmailInvalid")]
    public string? ExecutorEmail { get; set; }
}

