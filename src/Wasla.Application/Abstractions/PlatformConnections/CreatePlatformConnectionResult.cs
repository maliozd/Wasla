namespace Wasla.Application.Abstractions.PlatformConnections;

public sealed class CreatePlatformConnectionResult
{
    public bool Succeeded { get; init; }
    public Guid? Id { get; init; }

    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; } // e.g. "Duplicate"
}

