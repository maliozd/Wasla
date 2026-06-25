namespace Wasla.Application.Abstractions.Email;

public sealed class EmailMessage
{
    public required string ToEmail { get; init; }

    public string? ToName { get; init; }

    public required string Subject { get; init; }

    public required string HtmlBody { get; init; }

    public required string TextBody { get; init; }

    /// <summary>
    /// Sensitive messages may contain one-time URLs or tokens. Development log senders must not preview their body.
    /// </summary>
    public bool IsSensitive { get; init; }
}
