using System.Net;
using System.Reflection;
using System.Text;

namespace Wasla.Infrastructure.Email;

public sealed class EmailTemplateRenderer
{
    private static readonly Assembly Assembly = typeof(EmailTemplateRenderer).Assembly;
    private const string ResourcePrefix = "Wasla.Infrastructure.Email.Templates.";

    public string Render(string templateFileName, IReadOnlyDictionary<string, string> tokens, bool htmlEncodeValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateFileName);

        var content = LoadTemplate(templateFileName);
        return ReplaceTokens(content, tokens, htmlEncodeValues);
    }

    private static string LoadTemplate(string templateFileName)
    {
        var resourceName = ResourcePrefix + templateFileName;
        using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Email template not found: {templateFileName}. Expected embedded resource '{resourceName}'.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ReplaceTokens(
        string content,
        IReadOnlyDictionary<string, string> tokens,
        bool htmlEncodeValues)
    {
        foreach (var (key, value) in tokens)
        {
            var replacement = htmlEncodeValues
                ? WebUtility.HtmlEncode(value ?? string.Empty)
                : value ?? string.Empty;
            content = content.Replace("{{" + key + "}}", replacement, StringComparison.Ordinal);
        }

        return content;
    }
}
