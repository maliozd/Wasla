using System.ComponentModel.DataAnnotations;
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

    // Secrets are never shown; empty keeps existing.
    [Display(Name = "PlatformConnections.ApiKey")]
    public string? ApiKey { get; set; }

    [Display(Name = "PlatformConnections.ApiSecret")]
    public string? ApiSecret { get; set; }

    [Display(Name = "PlatformConnections.SupplierId")]
    public string? SupplierId { get; set; }

    [Display(Name = "PlatformConnections.ExecutorEmail")]
    [EmailAddress(ErrorMessage = "Validation.ExecutorEmailInvalid")]
    public string? ExecutorEmail { get; set; }
}

