using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Contact;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

/// <summary>
/// Messages sent through the public contact form and pilot applications from <c>/pilot</c> (WAS-217), newest first,
/// all together or one kind at a time. The only change an administrator can make is to send a team notification again
/// after it failed or was not configured.
/// </summary>
[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin/contact-messages")]
public sealed class ContactMessagesController : Controller
{
    public const string SuccessKey = "Admin.ContactMessages.Success";
    public const string ErrorKey = "Admin.ContactMessages.Error";

    private readonly ICentralAdminContactMessageService _service;
    private readonly IContactMessageService _messages;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ContactMessagesController(
        ICentralAdminContactMessageService service,
        IContactMessageService messages,
        IStringLocalizer<SharedResource> localizer)
    {
        _service = service;
        _messages = messages;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] int page = 1, [FromQuery] string? kind = null, CancellationToken ct = default)
    {
        var filter = kind switch
        {
            "pilot" => AdminContactMessageFilter.PilotApplications,
            "contact" => AdminContactMessageFilter.ContactMessages,
            "attention" => AdminContactMessageFilter.NotificationNeedsAttention,
            _ => AdminContactMessageFilter.All
        };
        var result = await _service.GetListAsync(page, AdminContactMessagePaging.MaxPageSize, filter, ct).ConfigureAwait(false);
        return View(new AdminContactMessageListViewModel
        {
            Items = result.Items,
            Page = result.Page,
            TotalPages = result.TotalPages,
            TotalCount = result.TotalCount,
            Filter = filter
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

    /// <summary>
    /// Sends the team notification of a stored message again (WAS-217). One attempt runs at a time for a message, so a
    /// double click or two administrators send it once; a notification already sent is not sent again.
    /// </summary>
    [HttpPost("{id:guid}/resend-notification")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendNotification(Guid id, CancellationToken ct)
    {
        var result = await _messages.ResendNotificationAsync(id, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ContactNotificationResendOutcome.NotFound:
                return NotFound(_localizer["Admin.ContactMessages.NotFound"].Value);
            case ContactNotificationResendOutcome.AlreadySent:
                TempData[ErrorKey] = _localizer["Admin.ContactMessages.ResendAlreadySent"].Value;
                break;
            case ContactNotificationResendOutcome.InProgress:
                TempData[ErrorKey] = _localizer["Admin.ContactMessages.ResendInProgress"].Value;
                break;
            default:
                var key = result.NotificationStatus switch
                {
                    Domain.Enums.ContactMessageNotificationStatus.Sent => "Admin.ContactMessages.ResendSent",
                    Domain.Enums.ContactMessageNotificationStatus.NotConfigured => "Admin.ContactMessages.ResendNotConfigured",
                    _ => "Admin.ContactMessages.ResendFailed"
                };
                TempData[result.NotificationStatus == Domain.Enums.ContactMessageNotificationStatus.Sent ? SuccessKey : ErrorKey] = _localizer[key].Value;
                break;
        }

        return Redirect($"/admin/contact-messages/{id}");
    }
}
