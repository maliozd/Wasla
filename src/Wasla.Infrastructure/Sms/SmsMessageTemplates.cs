using System.Globalization;
using System.Reflection;
using System.Text;

namespace Wasla.Infrastructure.Sms;

/// <summary>
/// SMS texts (WAS-192), embedded like the e-mail templates: <c>Sms/Templates/{Name}.{language}.txt</c> for Turkish,
/// English, Arabic and Russian, with <c>{{Code}}</c> and <c>{{Minutes}}</c>. Lines starting with <c>#</c> are template
/// comments and are never sent. The wording is a draft until WAS-193 approves it.
/// </summary>
/// <remarks>
/// A culture without its own template uses Turkish, the default culture. Without a Turkish template there is no text,
/// and the caller fails closed.
/// </remarks>
public sealed class SmsMessageTemplates
{
    public const string RegistrationPhoneVerification = "RegistrationPhoneVerification";

    public const string DefaultLanguage = "tr";

    private const string ResourcePrefix = "Wasla.Infrastructure.Sms.Templates.";

    private readonly Assembly _assembly;

    public SmsMessageTemplates()
        : this(typeof(SmsMessageTemplates).Assembly)
    {
    }

    internal SmsMessageTemplates(Assembly assembly)
    {
        _assembly = assembly;
    }

    /// <summary>The verification text for <paramref name="culture"/>, or null when not even the Turkish template exists.</summary>
    public string? RegistrationVerificationText(CultureInfo culture, string code, int minutes)
    {
        var template = Load(RegistrationPhoneVerification, culture.TwoLetterISOLanguageName)
                       ?? Load(RegistrationPhoneVerification, DefaultLanguage);
        if (template is null)
            return null;

        return template
            .Replace("{{Code}}", code, StringComparison.Ordinal)
            .Replace("{{Minutes}}", minutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private string? Load(string name, string language)
    {
        using var stream = _assembly.GetManifestResourceStream($"{ResourcePrefix}{name}.{language}.txt");
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.TrimEnd());
        var text = string.Join("\n", lines).Trim();
        return text.Length == 0 ? null : text;
    }
}
