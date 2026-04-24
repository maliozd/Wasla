namespace OrderHub.Application.Platform.Dtos;

public sealed record ExternalOrderItemDto(
    string ExternalItemId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    string? Notes,
    IReadOnlyCollection<ExternalOrderItemOptionDto> Options);

