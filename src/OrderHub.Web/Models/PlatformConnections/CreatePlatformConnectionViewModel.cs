using System.ComponentModel.DataAnnotations;
using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.PlatformConnections;

public sealed class CreatePlatformConnectionViewModel
{
    [Required(ErrorMessage = "Platform seçin.")]
    [Display(Name = "Platform")]
    public FoodPlatform Platform { get; set; }

    [Required(ErrorMessage = "Mağaza/Restoran ID gerekli.")]
    [Display(Name = "Mağaza ID")]
    public string StoreId { get; set; } = string.Empty;

    [Required(ErrorMessage = "API Key gerekli.")]
    [Display(Name = "API Key")]
    public string ApiKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "API Secret gerekli.")]
    [Display(Name = "API Secret")]
    public string ApiSecret { get; set; } = string.Empty;

    [Display(Name = "Aktif")]
    public bool IsActive { get; set; } = true;
}

