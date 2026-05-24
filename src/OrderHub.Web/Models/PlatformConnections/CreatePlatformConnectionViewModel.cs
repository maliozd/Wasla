using System.ComponentModel.DataAnnotations;
using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.PlatformConnections;

public sealed class CreatePlatformConnectionViewModel
{
    [Required(ErrorMessage = "Validation.PlatformRequired")]
    [Display(Name = "PlatformConnections.Platform")]
    public FoodPlatform Platform { get; set; }

    [Required(ErrorMessage = "Validation.StoreIdRequired")]
    [Display(Name = "PlatformConnections.StoreId")]
    public string StoreId { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.ApiKeyRequired")]
    [Display(Name = "PlatformConnections.ApiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.ApiSecretRequired")]
    [Display(Name = "PlatformConnections.ApiSecret")]
    public string ApiSecret { get; set; } = string.Empty;

    [Display(Name = "Common.Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "PlatformConnections.SupplierId")]
    public string? SupplierId { get; set; }

    [Display(Name = "PlatformConnections.ExecutorEmail")]
    [EmailAddress(ErrorMessage = "Validation.ExecutorEmailInvalid")]
    public string? ExecutorEmail { get; set; }
}

