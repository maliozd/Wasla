using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Abstractions.ReferenceData;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

/// <summary>
/// Messages sent through the public contact form and pilot applications from <c>/pilot</c> (WAS-217), newest first,
/// all together or one kind at a time. An administrator can send a failed or unconfigured team notification again, and
/// for a pilot application (WAS-222) set the team's status, record a contact with the restaurant, and handle a data
/// subject's request: find the person's application, download its data, correct it or delete it. Identity verification
/// before a request is answered is manual; nothing here sends the data anywhere. Searches, downloads and deletions are
/// posted with the antiforgery token, so a link cannot trigger them and no email address or phone number lands in an
/// address.
/// </summary>
[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin/contact-messages")]
public sealed class ContactMessagesController : Controller
{
    public const string SuccessKey = "Admin.ContactMessages.Success";
    public const string ErrorKey = "Admin.ContactMessages.Error";

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Letters of every script stay readable; markup characters are still escaped.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ICentralAdminContactMessageService _service;
    private readonly IContactMessageService _messages;
    private readonly ICentralAdminPilotApplicationService _pilots;
    private readonly ITurkishProvinceCatalog _provinces;
    private readonly TimeProvider _time;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<ContactMessagesController> _logger;

    public ContactMessagesController(
        ICentralAdminContactMessageService service,
        IContactMessageService messages,
        ICentralAdminPilotApplicationService pilots,
        ITurkishProvinceCatalog provinces,
        TimeProvider time,
        IStringLocalizer<SharedResource> localizer,
        ILogger<ContactMessagesController> logger)
    {
        _service = service;
        _messages = messages;
        _pilots = pilots;
        _provinces = provinces;
        _time = time;
        _localizer = localizer;
        _logger = logger;
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
                    ContactMessageNotificationStatus.Sent => "Admin.ContactMessages.ResendSent",
                    ContactMessageNotificationStatus.NotConfigured => "Admin.ContactMessages.ResendNotConfigured",
                    _ => "Admin.ContactMessages.ResendFailed"
                };
                TempData[result.NotificationStatus == ContactMessageNotificationStatus.Sent ? SuccessKey : ErrorKey] = _localizer[key].Value;
                break;
        }

        return Redirect(DetailPath(id));
    }

    /// <summary>Sets the team's status of a pilot application; a change restarts its retention period (WAS-222).</summary>
    [HttpPost("{id:guid}/status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(Guid id, [FromForm] string? status, CancellationToken ct)
    {
        if (!Enum.TryParse<PilotApplicationStatus>(status, ignoreCase: true, out var value)
            || !Enum.IsDefined(value)
            || value == PilotApplicationStatus.New
            || int.TryParse(status, out _))
        {
            TempData[ErrorKey] = _localizer["Admin.Pilot.Invalid"].Value;
            return Redirect(DetailPath(id));
        }

        var outcome = await _pilots.SetStatusAsync(id, value, ct).ConfigureAwait(false);
        return outcome switch
        {
            PilotApplicationChangeOutcome.NotFound => NotFound(_localizer["Admin.ContactMessages.NotFound"].Value),
            PilotApplicationChangeOutcome.Changed => Done(id, SuccessKey, "Admin.Pilot.StatusSaved"),
            _ => Done(id, ErrorKey, "Admin.Pilot.StatusUnchanged")
        };
    }

    /// <summary>Records a contact with the restaurant now, which restarts the retention period (WAS-222).</summary>
    [HttpPost("{id:guid}/record-contact")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordContact(Guid id, CancellationToken ct)
    {
        var outcome = await _pilots.RecordContactAsync(id, ct).ConfigureAwait(false);
        return outcome == PilotApplicationChangeOutcome.NotFound
            ? NotFound(_localizer["Admin.ContactMessages.NotFound"].Value)
            : Done(id, SuccessKey, "Admin.Pilot.ContactRecorded");
    }

    [HttpGet("find")]
    public IActionResult Find() => View(new AdminPilotFindViewModel());

    /// <summary>Finds one person's pilot applications by the exact email address or phone number (WAS-222).</summary>
    [HttpPost("find")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Find([FromForm] AdminPilotFindViewModel model, CancellationToken ct)
    {
        var email = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email.Trim();
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(model.Phone)
            && !(Application.Contact.ContactPhoneNumber.TryNormalize(model.Phone, out phone) && phone is not null))
        {
            ModelState.AddModelError(nameof(model.Phone), _localizer["Validation.ContactPhoneInvalid"].Value);
        }

        if (email is null && string.IsNullOrWhiteSpace(model.Phone))
            ModelState.AddModelError(string.Empty, _localizer["Admin.Pilot.FindNeedsValue"].Value);

        NoStore();
        if (!ModelState.IsValid)
            return View(model);

        model.Results = await _pilots.FindAsync(email, phone, ct).ConfigureAwait(false);
        model.Searched = true;
        return View(model);
    }

    /// <summary>
    /// Downloads everything stored about one pilot application as JSON, for a data subject's access request (WAS-222).
    /// JSON, not CSV, so no spreadsheet runs a typed value as a formula. The response is never cached, and the log names
    /// the application and the administrator only.
    /// </summary>
    [HttpPost("{id:guid}/export")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct)
    {
        if (!CentralAdminSessionClaims.TryRead(User, out var session))
            return Forbid(AuthSchemes.CentralAdmin);

        var export = await _pilots.ExportAsync(id, ct).ConfigureAwait(false);
        if (export is null)
            return NotFound(_localizer["Admin.ContactMessages.NotFound"].Value);

        _logger.LogInformation(
            "Pilot application {ContactMessageId} exported for a data subject request by Central Admin user {AdminUserId}.",
            id,
            session.AdminUserId);

        var document = new
        {
            format = "wasla-pilot-application",
            version = 1,
            exportedAtUtc = _time.GetUtcNow().UtcDateTime,
            application = export
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ExportJson);
        NoStore();
        return File(bytes, "application/json; charset=utf-8", $"pilot-application-{id:N}.json");
    }

    [HttpGet("{id:guid}/correct")]
    public async Task<IActionResult> Correct(Guid id, CancellationToken ct)
    {
        var message = await _service.GetDetailAsync(id, ct).ConfigureAwait(false);
        if (message is null || message.Topic != ContactTopic.PilotApplication)
            return NotFound(_localizer["Admin.ContactMessages.NotFound"].Value);

        NoStore();
        return View(new AdminPilotCorrectionViewModel
        {
            Id = id,
            RestaurantName = message.RestaurantName,
            ContactName = message.FullName,
            Email = message.Email,
            Phone = message.Phone ?? string.Empty,
            CityPlateCode = message.CityPlateCode ?? string.Empty,
            Note = message.Message,
            Provinces = await _provinces.GetActiveProvincesAsync(ct).ConfigureAwait(false)
        });
    }

    /// <summary>Corrects a pilot application's personal data on the data subject's request (WAS-222).</summary>
    [HttpPost("{id:guid}/correct")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Correct(Guid id, [FromForm] AdminPilotCorrectionViewModel model, CancellationToken ct)
    {
        model.Id = id;
        model.Provinces = await _provinces.GetActiveProvincesAsync(ct).ConfigureAwait(false);
        foreach (var (field, errorKey) in model.Validate())
            ModelState.AddModelError(field, errorKey);
        LocalizeModelStateErrors();

        if (!ModelState.IsValid)
        {
            NoStore();
            return View(model);
        }

        var outcome = await _pilots.CorrectAsync(
            id,
            new PilotApplicationCorrection(model.RestaurantName, model.ContactName, model.Email, model.NormalizedPhone(), model.CityPlateCode, model.Note),
            ct).ConfigureAwait(false);

        switch (outcome)
        {
            case PilotApplicationChangeOutcome.NotFound:
                return NotFound(_localizer["Admin.ContactMessages.NotFound"].Value);
            case PilotApplicationChangeOutcome.Invalid:
                ModelState.AddModelError(string.Empty, _localizer["Admin.Pilot.Invalid"].Value);
                NoStore();
                return View(model);
            default:
                return Done(id, SuccessKey, "Admin.Pilot.Corrected");
        }
    }

    /// <summary>
    /// Deletes a pilot application on the data subject's request (WAS-222), after the administrator confirms that the
    /// requester's identity was verified. The deletion is recorded with the administrator's id and no application data.
    /// </summary>
    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, [FromForm] bool identityVerified, CancellationToken ct)
    {
        if (!CentralAdminSessionClaims.TryRead(User, out var session))
            return Forbid(AuthSchemes.CentralAdmin);

        if (!identityVerified)
            return Done(id, ErrorKey, "Admin.Pilot.IdentityRequired");

        var outcome = await _pilots.DeleteOnRequestAsync(id, session.AdminUserId, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case PilotApplicationChangeOutcome.NotFound:
                return NotFound(_localizer["Admin.ContactMessages.NotFound"].Value);
            case PilotApplicationChangeOutcome.InProgress:
                return Done(id, ErrorKey, "Admin.Pilot.DeleteInProgress");
            default:
                TempData[SuccessKey] = _localizer["Admin.Pilot.Deleted"].Value;
                return Redirect("/admin/contact-messages?kind=pilot");
        }
    }

    private static string DetailPath(Guid id) => $"/admin/contact-messages/{id}";

    private RedirectResult Done(Guid id, string tempDataKey, string resourceKey)
    {
        TempData[tempDataKey] = _localizer[resourceKey].Value;
        return Redirect(DetailPath(id));
    }

    /// <summary>Pages and files that hold an applicant's personal data are not kept by the browser or a proxy.</summary>
    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
    }

    private void LocalizeModelStateErrors()
    {
        foreach (var state in ModelState.Values)
        {
            var errors = state.Errors.ToList();
            if (errors.Count == 0)
                continue;

            state.Errors.Clear();
            foreach (var error in errors)
                state.Errors.Add(_localizer[error.ErrorMessage].Value);
        }
    }
}
