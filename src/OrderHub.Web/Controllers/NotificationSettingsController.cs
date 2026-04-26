using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.Notifications;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Web.Models.Notifications;

namespace OrderHub.Web.Controllers;

[Authorize]
[Route("notification-settings")]
public sealed class NotificationSettingsController : BaseController
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IUserNotificationSettingsService _settings;
    private readonly IValidator<UpdateNotificationSettingsCommand> _validator;

    public NotificationSettingsController(
        ICurrentCustomerService currentCustomer,
        IUserNotificationSettingsService settings,
        IValidator<UpdateNotificationSettingsCommand> validator)
    {
        _currentCustomer = currentCustomer;
        _settings = settings;
        _validator = validator;
    }

    [HttpGet("")]
    public async Task<IActionResult> Get(CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var s = await _settings.GetAsync(customer.Id, CurrentUserId, ct);

        var vm = new NotificationSettingsViewModel
        {
            NewOrderSoundEnabled = s.NewOrderSoundEnabled,
            NewOrderSoundName = s.NewOrderSoundName,
            NewOrderSoundRepeatCount = s.NewOrderSoundRepeatCount,
            NewOrderSoundVolume = s.NewOrderSoundVolume,
            ShowBrowserNotification = s.ShowBrowserNotification
        };

        return PartialView("_NotificationSettingsModal", vm);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("")]
    public async Task<IActionResult> Post([FromForm] NotificationSettingsViewModel model, CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var cmd = new UpdateNotificationSettingsCommand
        {
            NewOrderSoundEnabled = model.NewOrderSoundEnabled,
            NewOrderSoundName = model.NewOrderSoundName ?? "bell",
            NewOrderSoundRepeatCount = model.NewOrderSoundRepeatCount,
            NewOrderSoundVolume = model.NewOrderSoundVolume,
            ShowBrowserNotification = model.ShowBrowserNotification
        };

        var validation = await _validator.ValidateAsync(cmd, ct);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors)
                ModelState.AddModelError(e.PropertyName, e.ErrorMessage);
            return PartialView("_NotificationSettingsModal", model);
        }

        await _settings.UpdateAsync(customer.Id, CurrentUserId, cmd, ct);

        ViewData["Saved"] = true;
        return PartialView("_NotificationSettingsModal", model);
    }
}

