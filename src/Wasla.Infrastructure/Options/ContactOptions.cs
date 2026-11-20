using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Options;

/// <summary>The public contact form and the pilot application form, bound from <c>Wasla:Contact</c>.</summary>
public sealed class ContactOptions
{
    public const string SectionName = "Wasla:Contact";

    /// <summary>The address that receives a notification email for every contact message.</summary>
    public string RecipientEmail { get; set; } = string.Empty;

    /// <summary>
    /// The team role address that receives a notification email for every pilot application (WAS-217). Optional: when
    /// empty, pilot applications go to <see cref="RecipientEmail"/>.
    /// </summary>
    public string? PilotRecipientEmail { get; set; }
}

/// <summary>The host does not start without a valid recipient address for contact messages, or with an invalid pilot address.</summary>
public sealed class ContactOptionsValidator : IValidateOptions<ContactOptions>
{
    public ValidateOptionsResult Validate(string? name, ContactOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (!IsPlainAddress(options.RecipientEmail))
            failures.Add($"{ContactOptions.SectionName}:{nameof(ContactOptions.RecipientEmail)} must be one email address.");
        if (!string.IsNullOrWhiteSpace(options.PilotRecipientEmail) && !IsPlainAddress(options.PilotRecipientEmail))
            failures.Add($"{ContactOptions.SectionName}:{nameof(ContactOptions.PilotRecipientEmail)} must be empty or one email address.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
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
