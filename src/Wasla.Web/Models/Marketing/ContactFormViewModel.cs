using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Marketing;

/// <summary>
/// The public contact form. Error messages are resource keys, localized by the controller, which also checks the phone
/// number, the topic and the privacy notice acknowledgement (<see cref="Validate"/>) so that every error shows at once.
/// <see cref="Website"/> is a field people never see; a submission that fills it is treated as automated.
/// </summary>
public sealed class ContactFormViewModel
{
    public static readonly ContactTopic[] Topics =
        [ContactTopic.WaslaOrders, ContactTopic.WaslaPos, ContactTopic.PrintBridgeSetup, ContactTopic.Pricing, ContactTopic.Other];

    [Required(ErrorMessage = "Validation.FullNameRequired")]
    [StringLength(ContactMessageLimits.FullNameMaxLength, ErrorMessage = "Validation.TooLong")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.RestaurantNameRequired")]
    [StringLength(ContactMessageLimits.RestaurantNameMaxLength, ErrorMessage = "Validation.TooLong")]
    public string RestaurantName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.EmailRequired")]
    [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
    [StringLength(ContactMessageLimits.EmailMaxLength, ErrorMessage = "Validation.TooLong")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Optional Turkish phone number as typed; stored as ten digits.</summary>
    [StringLength(32, ErrorMessage = "Validation.ContactPhoneInvalid")]
    public string? Phone { get; set; }

    public ContactTopic Topic { get; set; } = ContactTopic.WaslaOrders;

    [Required(ErrorMessage = "Validation.ContactMessageRequired")]
    [StringLength(ContactMessageLimits.MessageMaxLength, ErrorMessage = "Validation.ContactMessageTooLong")]
    public string Message { get; set; } = string.Empty;

    public bool PrivacyNoticeAcknowledged { get; set; }

    /// <summary>Honeypot. Hidden from people and assistive technology; real visitors leave it empty.</summary>
    public string? Website { get; set; }

    /// <summary>The message was stored (set after the redirect that follows a successful submission).</summary>
    [BindNever]
    public bool Sent { get; set; }

    /// <summary>Storing the message failed; the form is shown again with what the visitor typed.</summary>
    [BindNever]
    public bool SendFailed { get; set; }

    /// <summary>The ten stored digits of <see cref="Phone"/>, or null; valid only after validation passed.</summary>
    public string? NormalizedPhone() => ContactPhoneNumber.TryNormalize(Phone, out var digits) ? digits : null;

    /// <summary>The checks the attributes cannot express, as (field, resource key) pairs.</summary>
    public IEnumerable<(string Field, string ErrorKey)> Validate()
    {
        if (!ContactPhoneNumber.TryNormalize(Phone, out _))
            yield return (nameof(Phone), "Validation.ContactPhoneInvalid");

        if (!Enum.IsDefined(Topic))
            yield return (nameof(Topic), "Validation.ContactTopicInvalid");

        if (!PrivacyNoticeAcknowledged)
            yield return (nameof(PrivacyNoticeAcknowledged), "Validation.PrivacyNoticeRequired");
    }
}
