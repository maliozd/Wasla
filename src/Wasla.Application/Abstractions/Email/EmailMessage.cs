namespace Wasla.Application.Abstractions.Email;

public sealed class EmailMessage
{
    public required string ToEmail { get; init; }

    public string? ToName { get; init; }

    public required string Subject { get; init; }

    public required string HtmlBody { get; init; }

    public required string TextBody { get; init; }
}
