using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Ui;

public sealed record OrderStatusBadgeModel(OrderStatus Status, string? Label = null);
