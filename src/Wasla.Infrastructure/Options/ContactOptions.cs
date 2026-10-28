using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Options;

/// <summary>The public contact form, bound from <c>Wasla:Contact</c>.</summary>
public sealed class ContactOptions
{
    public const string SectionName = "Wasla:Contact";

    /// <summary>The address that receives a notification email for every contact message.</summary>
    public string RecipientEmail { get; set; } = string.Empty;
}

/// <summary>The host does not start without a valid recipient address for contact messages.</summary>
public sealed class ContactOptionsValidator : IValidateOptions<ContactOptions>
{
    public ValidateOptionsResult Validate(string? name, ContactOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return IsPlainAddress(options.RecipientEmail)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{ContactOptions.SectionName}:{nameof(ContactOptions.RecipientEmail)} must be one email address.");
    }

    private static bool IsPlainAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return MailAddress.TryCreate(trimmed, out var address)
            && string.Equals(address.Address, trimmed, StringComparison.Ordinal)
            && string.IsNullOrEmpty(address.DisplayName);
    }
}
