using System.ComponentModel.DataAnnotations;

namespace Wasla.Web.Models.Branches;

public sealed class CreateBranchViewModel
{
    [Required(ErrorMessage = "Validation.BranchNameRequired")]
    [Display(Name = "Branches.Name")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.AddressRequired")]
    [Display(Name = "Branches.Address")]
    public string Address { get; set; } = string.Empty;

    [Display(Name = "Common.Active")]
    public bool IsActive { get; set; } = true;
}

