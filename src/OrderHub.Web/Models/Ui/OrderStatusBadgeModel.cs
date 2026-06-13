using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.Ui;

public sealed record OrderStatusBadgeModel(OrderStatus Status, string? Label = null);
