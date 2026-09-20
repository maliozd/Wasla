using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Orders;

public sealed class OrderListViewModel
{
    public OrderFilterViewModel Filters { get; set; } = new();
    public List<Row> Orders { get; set; } = new();

    public int TotalCount { get; set; }

    /// <summary>Restaurant local (Turkey) calendar date used for "default live" and empty-state copy.</summary>
    public DateOnly TurkeyLocalToday { get; set; }

    /// <summary>When no rows, use a simple "no orders" string instead of "no matches for filters" (e.g. default today, no extra filters).</summary>
    public bool UseSimpleNoOrdersMessage { get; set; }

    /// <summary>Base path for list and sort/pagination links.</summary>
    public string ListBasePath { get; set; } = "/orders";

    public int TotalPages => Filters.PageSize > 0
        ? Math.Max(1, (int)Math.Ceiling((double)TotalCount / Filters.PageSize))
        : 1;

    public sealed class Row
    {
        public Guid Id { get; set; }
        public FoodPlatform Platform { get; set; }
        public string ExternalOrderCode { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime ReceivedAtUtc { get; set; }

        /// <summary>Received time in Turkey local zone for table display (DB stores UTC in <see cref="ReceivedAtUtc" />).</summary>
        public DateTime ReceivedAtLocal { get; set; }

        /// <summary>Display-only: number of line items on the order.</summary>
        public int ItemCount { get; set; }

        /// <summary>Display-only: first line item product name for card image fallback.</summary>
        public string? FirstProductName { get; set; }

        /// <summary>Display-only: resolved product/card image URL for compact/kitchen views.</summary>
        public string DisplayImageUrl { get; set; } = string.Empty;

        /// <summary>Line items for operational Live Screen cards (Orders table ignores this).</summary>
        public List<LineItem> LineItems { get; set; } = new();
    }

    public sealed class LineItem
    {
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string? Notes { get; set; }
    }
}

