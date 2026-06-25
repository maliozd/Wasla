using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Email;
using Wasla.Infrastructure.Options;

namespace Wasla.Infrastructure.Email;

public sealed class PasswordResetEmailFactory
{
    private readonly EmailTemplateRenderer _renderer;
    private readonly EmailOptions _options;

    public PasswordResetEmailFactory(EmailTemplateRenderer renderer, IOptions<EmailOptions> options)
    {
        _renderer = renderer;
        _options = options.Value;
    }

    public EmailMessage BuildPasswordResetEmail(
        string toEmail,
        string ownerFullName,
        string resetUrl,
        string? cultureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resetUrl);

        var templateCulture = ResolveTemplateCulture(cultureName);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OwnerFullName"] = ownerFullName.Trim(),
            ["PasswordResetUrl"] = resetUrl.Trim(),
            ["FromName"] = _options.FromName.Trim()
        };

        return new EmailMessage
        {
            ToEmail = toEmail.Trim(),
            ToName = ownerFullName.Trim(),
            Subject = ResolveSubject(templateCulture),
            HtmlBody = _renderer.Render($"PasswordReset.{templateCulture}.html", tokens, htmlEncodeValues: true),
            TextBody = _renderer.Render($"PasswordReset.{templateCulture}.txt", tokens, htmlEncodeValues: false),
            IsSensitive = true
        };
    }

    private static string ResolveTemplateCulture(string? cultureName)
    {
        if (cultureName?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true)
            return "en";

        if (cultureName?.StartsWith("ar", StringComparison.OrdinalIgnoreCase) == true)
            return "ar";

        return "tr";
    }

    private static string ResolveSubject(string templateCulture) => templateCulture switch
    {
        "en" => "Your Wasla password reset link",
        "ar" => "رابط إعادة تعيين كلمة المرور في Wasla",
        _ => "Wasla şifre sıfırlama bağlantınız"
    };
}
