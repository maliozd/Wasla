using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Contact;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

/// <summary>Messages sent through the public contact form, newest first. Read-only.</summary>
[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin/contact-messages")]
public sealed class ContactMessagesController : Controller
{
    private readonly ICentralAdminContactMessageService _service;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ContactMessagesController(ICentralAdminContactMessageService service, IStringLocalizer<SharedResource> localizer)
    {
        _service = service;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] int page = 1, CancellationToken ct = default)
    {
        var result = await _service.GetListAsync(page, AdminContactMessagePaging.MaxPageSize, ct).ConfigureAwait(false);
        return View(new AdminContactMessageListViewModel
        {
            Items = result.Items,
            Page = result.Page,
            TotalPages = result.TotalPages,
            TotalCount = result.TotalCount
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var message = await _service.GetDetailAsync(id, ct).ConfigureAwait(false);
        if (message is null)
            return NotFound(_localizer["Admin.ContactMessages.NotFound"].Value);

        return View(new AdminContactMessageDetailViewModel { Message = message });
    }
}
