using System.Globalization;
using System.Text;

namespace Wasla.Web.Ui;

/// <summary>
/// Compact initials for tenant user avatars. Presentation-only; no persistence.
/// </summary>
public static class UserInitials
{
    public const int ToneCount = 5;

    public static string FromDisplayName(
        string? fullName,
        string? email = null,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;

        var source = FirstNonEmpty(fullName, EmailLocalPartAsName(email));
        if (string.IsNullOrEmpty(source))
            return "?";

        var parts = source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "?";

        if (parts.Length == 1)
            return UppercaseGrapheme(parts[0], culture);

        return UppercaseGrapheme(parts[0], culture) + UppercaseGrapheme(parts[^1], culture);
    }

    public static int ToneIndex(Guid id) => id.ToByteArray()[0] % ToneCount;

    private static string? EmailLocalPartAsName(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var at = email.IndexOf('@');
        var local = at > 0 ? email[..at] : email.Trim();
        if (string.IsNullOrWhiteSpace(local))
            return null;

        var builder = new StringBuilder(local.Length);
        foreach (var ch in local)
            builder.Append(ch is '.' or '_' or '-' or '+' ? ' ' : ch);

        return builder.ToString();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static string UppercaseGrapheme(string word, CultureInfo culture)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(word);
        if (!enumerator.MoveNext())
            return "?";

        var element = enumerator.GetTextElement();
        return string.IsNullOrEmpty(element)
            ? "?"
            : element.ToUpper(culture);
    }
}
