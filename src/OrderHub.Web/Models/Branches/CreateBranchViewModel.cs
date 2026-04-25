using System.ComponentModel.DataAnnotations;

namespace OrderHub.Web.Models.Branches;

public sealed class CreateBranchViewModel
{
    [Required(ErrorMessage = "Şube adı gerekli.")]
    [Display(Name = "Şube Adı")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Adres gerekli.")]
    [Display(Name = "Adres")]
    public string Address { get; set; } = string.Empty;

    [Display(Name = "Aktif")]
    public bool IsActive { get; set; } = true;
}

