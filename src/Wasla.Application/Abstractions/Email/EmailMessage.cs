namespace Wasla.Application.Abstractions.Email;

public sealed class EmailMessage
{
    public string ToEmail { get; init; } = string.Empty;
    public string ToName { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string HtmlBody { get; init; } = string.Empty;
    public string TextBody { get; init; } = string.Empty;
}
