using Wasla.Application.Abstractions.Contact;

namespace Wasla.Web.Areas.Admin.Models;

public sealed class AdminContactMessageListViewModel
{
    public IReadOnlyList<AdminContactMessageListItemDto> Items { get; set; } = [];
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }
}

public sealed class AdminContactMessageDetailViewModel
{
    public required AdminContactMessageDetailDto Message { get; init; }
}
