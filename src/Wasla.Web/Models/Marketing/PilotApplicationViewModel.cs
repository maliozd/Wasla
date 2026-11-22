using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wasla.Application.Abstractions.ReferenceData;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;

namespace Wasla.Web.Models.Marketing;

/// <summary>
/// The pilot restaurant application form at <c>/pilot</c> (WAS-217). Error messages are resource keys, localized by the
/// controller, which also checks the phone number and the city (<see cref="Validate"/>) so that every error shows at
/// once. There is no consent or acknowledgement checkbox: the privacy notice is linked before the submit button.
/// <see cref="Website"/> is a field people never see; a submission that fills it is treated as automated.
/// </summary>
public sealed class PilotApplicationViewModel
{
    /// <summary>The fields in form order: the error summary lists errors in this order, and its links target these ids.</summary>
    public static readonly (string Field, string Id)[] FieldOrder =
    [
        (nameof(RestaurantName), "pilot-restaurant-name"),
        (nameof(CityPlateCode), "pilot-city"),
        (nameof(ContactName), "pilot-contact-name"),
        (nameof(Phone), "pilot-phone"),
        (nameof(Email), "pilot-email"),
        (nameof(Note), "pilot-note")
    ];

    /// <summary>
    /// Generated when the form is first shown and kept across validation errors and a failed save. It becomes the stored
    /// id, so posting the same form twice (a double click or a resubmitted page) stores one application.
    /// </summary>
    public Guid SubmissionId { get; set; }

    [Required(ErrorMessage = "Validation.RestaurantNameRequired")]
    [StringLength(ContactMessageLimits.RestaurantNameMaxLength, ErrorMessage = "Validation.TooLong")]
    public string RestaurantName { get; set; } = string.Empty;

    /// <summary>The province's two-digit plate code, one of <see cref="Provinces"/>.</summary>
    [Required(ErrorMessage = "Validation.PilotCityRequired")]
    public string CityPlateCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.PilotContactNameRequired")]
    [StringLength(ContactMessageLimits.FullNameMaxLength, ErrorMessage = "Validation.TooLong")]
    public string ContactName { get; set; } = string.Empty;

    /// <summary>Required Turkish phone number as typed after the fixed <c>+90</c>; stored as ten digits.</summary>
    [Required(ErrorMessage = "Validation.PilotPhoneRequired")]
    [StringLength(32, ErrorMessage = "Validation.ContactPhoneInvalid")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.EmailRequired")]
    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [StringLength(ContactMessageLimits.EmailMaxLength, ErrorMessage = "Validation.TooLong")]
    public string Email { get; set; } = string.Empty;

    [StringLength(ContactMessageLimits.MessageMaxLength, ErrorMessage = "Validation.PilotNoteTooLong")]
    public string? Note { get; set; }

    /// <summary>Honeypot. Hidden from people and assistive technology; real visitors leave it empty.</summary>
    public string? Website { get; set; }

    /// <summary>The active provinces the city list offers, by Turkish name.</summary>
    [BindNever]
    public IReadOnlyList<TurkishProvince> Provinces { get; set; } = [];

    /// <summary>The application was stored (set after the redirect that follows a successful submission).</summary>
    [BindNever]
    public bool Sent { get; set; }

    /// <summary>Storing the application failed; the form is shown again with what the visitor typed.</summary>
    [BindNever]
    public bool SaveFailed { get; set; }

    /// <summary>The ten stored digits of <see cref="Phone"/>; valid only after validation passed.</summary>
    public string NormalizedPhone() => ContactPhoneNumber.TryNormalize(Phone, out var digits) && digits is not null ? digits : string.Empty;

    /// <summary>The checks the attributes cannot express, as (field, resource key) pairs.</summary>
    public IEnumerable<(string Field, string ErrorKey)> Validate()
    {
        if (!string.IsNullOrWhiteSpace(Phone) && !(ContactPhoneNumber.TryNormalize(Phone, out var digits) && digits is not null))
            yield return (nameof(Phone), "Validation.ContactPhoneInvalid");

        var plate = CityPlateCode?.Trim();
        if (!string.IsNullOrEmpty(plate) && !Provinces.Any(p => p.PlateCode == plate))
            yield return (nameof(CityPlateCode), "Validation.PilotCityRequired");
    }
}
