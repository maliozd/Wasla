using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Application.Abstractions.Signup;

namespace OrderHub.Web.Models.Signup;

public sealed class SignupViewModel
{
    [Display(Name = "Signup.Plan")]
    public string PlanCode { get; set; } = OrderHubPlanCodes.Starter;

    [Display(Name = "Signup.BillingPeriod")]
    public string BillingPeriod { get; set; } = "Monthly";

    [Required(ErrorMessage = "Validation.BusinessNameRequired")]
    [Display(Name = "Signup.BusinessName")]
    public string BusinessName { get; set; } = string.Empty;

    [Display(Name = "Signup.BusinessType")]
    public List<string> SelectedBusinessTypeCodes { get; set; } = [];

    [Required(ErrorMessage = "Validation.SlugRequired")]
    [RegularExpression("^[a-z0-9][a-z0-9_-]*$", ErrorMessage = "Validation.SlugInvalid")]
    [Display(Name = "Signup.Slug")]
    public string Slug { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.Required")]
    [Display(Name = "Signup.Country")]
    public string Country { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.CityRequired")]
    [Display(Name = "Signup.CityLabel")]
    public int? CityId { get; set; }

    [Required(ErrorMessage = "Validation.DistrictRequired")]
    [Display(Name = "Signup.DistrictLabel")]
    public int? DistrictId { get; set; }

    public int? NeighborhoodId { get; set; }

    public int? StreetId { get; set; }

    public string? City { get; set; }

    public string? District { get; set; }

    [Display(Name = "Signup.Neighborhood")]
    public string? Neighborhood { get; set; }

    [Required(ErrorMessage = "Validation.StreetAddressRequired")]
    [Display(Name = "Signup.StreetAddress")]
    public string? StreetAddress { get; set; }

    [Display(Name = "Signup.BuildingNumber")]
    public string? BuildingNumber { get; set; }

    [Display(Name = "Signup.Floor")]
    public string? Floor { get; set; }

    [Display(Name = "Signup.DoorNumber")]
    public string? DoorNumber { get; set; }

    [Display(Name = "Signup.AddressNote")]
    public string? AddressNote { get; set; }

    [Display(Name = "Signup.PostalCode")]
    public string? PostalCode { get; set; }

    [Display(Name = "Signup.LocationUrl")]
    public string? LocationUrl { get; set; }

    [Display(Name = "Signup.BusinessPhoneType")]
    public string BusinessPhoneType { get; set; } = "Mobile";

    [Required(ErrorMessage = "Validation.Required")]
    [Display(Name = "Signup.BusinessPhone")]
    public string BusinessPhone { get; set; } = string.Empty;

    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [Display(Name = "Signup.BusinessEmail")]
    public string? BusinessEmail { get; set; }

    [Required(ErrorMessage = "Validation.FullNameRequired")]
    [Display(Name = "Signup.OwnerFullName")]
    public string OwnerFullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.EmailRequired")]
    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [Display(Name = "Signup.OwnerEmail")]
    public string OwnerEmail { get; set; } = string.Empty;

    [Display(Name = "Signup.OwnerPhone")]
    public string? OwnerPhone { get; set; }

    [Required(ErrorMessage = "Validation.PasswordRequired")]
    [MinLength(8, ErrorMessage = "Validation.PasswordMinLength")]
    [DataType(DataType.Password)]
    [Display(Name = "Signup.Password")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.ConfirmPasswordRequired")]
    [Compare(nameof(Password), ErrorMessage = "Validation.PasswordMismatch")]
    [DataType(DataType.Password)]
    [Display(Name = "Signup.ConfirmPassword")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string MarketingBaseDomain { get; set; } = "orderhub.local";

    public IReadOnlyList<SelectListItem> PlanOptions { get; set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SignupBusinessTypeOption> BusinessTypeOptions { get; set; } = Array.Empty<SignupBusinessTypeOption>();

    public IReadOnlyList<SignupCityOption> Cities { get; set; } = Array.Empty<SignupCityOption>();

    public IReadOnlyList<SelectListItem> CityOptions { get; set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> DistrictOptions { get; set; } = Array.Empty<SelectListItem>();

    public bool IsContactSalesPlan { get; set; }
}
