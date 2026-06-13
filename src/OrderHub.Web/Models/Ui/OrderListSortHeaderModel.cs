using OrderHub.Web.Models.Orders;

namespace OrderHub.Web.Models.Ui;

public sealed record OrderListSortHeaderModel(
    string BasePath,
    OrderFilterViewModel Filters,
    string SortBy,
    string Title);
