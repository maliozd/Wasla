using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Abstractions.ReferenceData;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;

namespace Wasla.Web.Areas.Admin.Models;

public sealed class AdminContactMessageListViewModel
{
    public IReadOnlyList<AdminContactMessageListItemDto> Items { get; set; } = [];
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }
    public AdminContactMessageFilter Filter { get; set; }

    /// <summary>The <c>kind</c> query value of <see cref="Filter"/>, or null for all messages.</summary>
    public string? KindQuery => Filter switch
    {
        AdminContactMessageFilter.PilotApplications => "pilot",
        AdminContactMessageFilter.ContactMessages => "contact",
        AdminContactMessageFilter.NotificationNeedsAttention => "attention",
        _ => null
    };
}

public sealed class AdminContactMessageDetailViewModel
{
    public required AdminContactMessageDetailDto Message { get; init; }
}

/// <summary>
/// The search for one person's pilot application (WAS-222). It is posted, not put in the address, so the email address
/// or phone number never lands in the browser history or a request log.
/// </summary>
public sealed class AdminPilotFindViewModel
{
    /// <summary>Compared exactly (case ignored); a longer value simply matches nothing.</summary>
    public string? Email { get; set; }

    /// <summary>A Turkish number as typed; it must normalize to ten digits.</summary>
    public string? Phone { get; set; }

    [BindNever]
    public bool Searched { get; set; }

    [BindNever]
    public IReadOnlyList<PilotApplicationSearchItem> Results { get; set; } = [];
}

/// <summary>The correction form of a pilot application (WAS-222), with the rules of the application form.</summary>
public sealed class AdminPilotCorrectionViewModel
{
    [BindNever]
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Validation.RestaurantNameRequired")]
    [StringLength(ContactMessageLimits.RestaurantNameMaxLength, ErrorMessage = "Validation.TooLong")]
    public string RestaurantName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.PilotContactNameRequired")]
    [StringLength(ContactMessageLimits.FullNameMaxLength, ErrorMessage = "Validation.TooLong")]
    public string ContactName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.EmailRequired")]
    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [StringLength(ContactMessageLimits.EmailMaxLength, ErrorMessage = "Validation.TooLong")]
    public string Email { get; set; } = string.Empty;

    /// <summary>A Turkish number after the fixed <c>+90</c>; stored as ten digits.</summary>
    [Required(ErrorMessage = "Validation.PilotPhoneRequired")]
    [StringLength(32, ErrorMessage = "Validation.ContactPhoneInvalid")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.PilotCityRequired")]
    public string CityPlateCode { get; set; } = string.Empty;

    [StringLength(ContactMessageLimits.MessageMaxLength, ErrorMessage = "Validation.PilotNoteTooLong")]
    public string? Note { get; set; }

    [BindNever]
    public IReadOnlyList<TurkishProvince> Provinces { get; set; } = [];

    /// <summary>The checks the attributes cannot express, as (field, resource key) pairs.</summary>
    public IEnumerable<(string Field, string ErrorKey)> Validate()
    {
        if (!string.IsNullOrWhiteSpace(Phone) && !(ContactPhoneNumber.TryNormalize(Phone, out var digits) && digits is not null))
            yield return (nameof(Phone), "Validation.ContactPhoneInvalid");

        var plate = CityPlateCode?.Trim();
        if (!string.IsNullOrEmpty(plate) && !Provinces.Any(p => p.PlateCode == plate))
            yield return (nameof(CityPlateCode), "Validation.PilotCityRequired");
    }

    public string NormalizedPhone() => ContactPhoneNumber.TryNormalize(Phone, out var digits) && digits is not null ? digits : string.Empty;
}
