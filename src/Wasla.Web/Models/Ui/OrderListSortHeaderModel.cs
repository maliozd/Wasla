using Wasla.Web.Models.Orders;

namespace Wasla.Web.Models.Ui;

public sealed record OrderListSortHeaderModel(
    string BasePath,
    OrderFilterViewModel Filters,
    string SortBy,
    string Title,
    bool IsNumeric = false);
