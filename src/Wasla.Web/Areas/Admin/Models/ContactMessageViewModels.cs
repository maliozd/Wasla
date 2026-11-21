using Wasla.Application.Abstractions.Contact;

namespace Wasla.Web.Areas.Admin.Models;

public sealed class AdminContactMessageListViewModel
{
    public IReadOnlyList<AdminContactMessageListItemDto> Items { get; set; } = [];
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }
    public AdminContactMessageFilter Filter { get; set; }

    /// <summary>The <c>kind</c> query value of <see cref="Filter"/>, or null for all messages.</summary>
    public string? KindQuery => Filter switch
    {
        AdminContactMessageFilter.PilotApplications => "pilot",
        AdminContactMessageFilter.ContactMessages => "contact",
        AdminContactMessageFilter.NotificationNeedsAttention => "attention",
        _ => null
    };
}

public sealed class AdminContactMessageDetailViewModel
{
    public required AdminContactMessageDetailDto Message { get; init; }
}
