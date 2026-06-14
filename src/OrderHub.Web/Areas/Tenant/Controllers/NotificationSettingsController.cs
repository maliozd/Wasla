using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.Notifications;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Web.Controllers;
using OrderHub.Web.Models.Notifications;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
[Route("notification-settings")]
public sealed class NotificationSettingsController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IUserNotificationSettingsService _settings;
    private readonly IValidator<UpdateNotificationSettingsCommand> _validator;
    private readonly IWebHostEnvironment _env;

    public NotificationSettingsController(
        ICurrentTenantService currentTenant,
        IUserNotificationSettingsService settings,
        IValidator<UpdateNotificationSettingsCommand> validator,
        IWebHostEnvironment env)
    {
        _currentTenant = currentTenant;
        _settings = settings;
        _validator = validator;
        _env = env;
    }

    [HttpGet("")]
    public async Task<IActionResult> Get(CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var s = await _settings.GetAsync(tenant.Id, CurrentUserId, ct);
        var sounds = ResolveSounds();

        var vm = new NotificationSettingsViewModel
        {
            NewOrderSoundEnabled = s.NewOrderSoundEnabled,
            NewOrderSoundName = s.NewOrderSoundName,
            NewOrderSoundRepeatCount = s.NewOrderSoundRepeatCount,
            NewOrderSoundVolumePercent = ToPercent(s.NewOrderSoundVolume),
            ShowBrowserNotification = s.ShowBrowserNotification,
            NewOrderHighlightColor = s.NewOrderHighlightColor,
            NewOrderHighlightBehavior = s.NewOrderHighlightBehavior,
            NewOrderHighlightDurationSeconds = s.NewOrderHighlightDurationSeconds,
            AvailableSounds = sounds
        };

        return PartialView("_NotificationSettingsModal", vm);
    }

    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var s = await _settings.GetAsync(tenant.Id, CurrentUserId, ct);
        var percent = ToPercent(s.NewOrderSoundVolume);
        var sounds = ResolveSounds();
        return Ok(new
        {
            newOrderSoundEnabled = s.NewOrderSoundEnabled,
            newOrderSoundName = s.NewOrderSoundName,
            newOrderSoundRepeatCount = s.NewOrderSoundRepeatCount,
            newOrderSoundVolume = s.NewOrderSoundVolume,
            newOrderSoundVolumePercent = percent,
            showBrowserNotification = s.ShowBrowserNotification,
            newOrderHighlightColor = s.NewOrderHighlightColor,
            newOrderHighlightBehavior = s.NewOrderHighlightBehavior,
            newOrderHighlightDurationSeconds = s.NewOrderHighlightDurationSeconds,
            availableSounds = sounds.Select(x => new { name = x.Name, label = x.Label, url = x.Url })
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("")]
    public async Task<IActionResult> Post([FromForm] NotificationSettingsViewModel model, CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        if (!IsValidPercent(model.NewOrderSoundVolumePercent))
        {
            ModelState.AddModelError(nameof(model.NewOrderSoundVolumePercent), "Ses seviyesi %25, %50, %75 veya %100 olmalıdır.");
        }

        var volume = model.NewOrderSoundVolumePercent / 100m;

        var cmd = new UpdateNotificationSettingsCommand
        {
            NewOrderSoundEnabled = model.NewOrderSoundEnabled,
            NewOrderSoundName = model.NewOrderSoundName ?? "bell1",
            NewOrderSoundRepeatCount = model.NewOrderSoundRepeatCount,
            NewOrderSoundVolume = volume,
            ShowBrowserNotification = model.ShowBrowserNotification,
            NewOrderHighlightColor = model.NewOrderHighlightColor,
            NewOrderHighlightBehavior = model.NewOrderHighlightBehavior,
            NewOrderHighlightDurationSeconds = model.NewOrderHighlightDurationSeconds
        };

        if (!ModelState.IsValid)
        {
            model.AvailableSounds = ResolveSounds();
            if (WantsSettingsJsonResponse()) Response.StatusCode = StatusCodes.Status400BadRequest;
            return PartialView("_NotificationSettingsModal", model);
        }

        var validation = await _validator.ValidateAsync(cmd, ct);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors)
                ModelState.AddModelError(e.PropertyName, e.ErrorMessage);
            model.AvailableSounds = ResolveSounds();
            if (WantsSettingsJsonResponse()) Response.StatusCode = StatusCodes.Status400BadRequest;
            return PartialView("_NotificationSettingsModal", model);
        }

        await _settings.UpdateAsync(tenant.Id, CurrentUserId, cmd, ct);

        if (WantsSettingsJsonResponse())
            return Json(new { success = true });

        model.AvailableSounds = ResolveSounds();
        return PartialView("_NotificationSettingsModal", model);
    }

    private bool WantsSettingsJsonResponse() =>
        string.Equals(Request.Headers["X-Requested-With"], "fetch", StringComparison.OrdinalIgnoreCase);

    [ValidateAntiForgeryToken]
    [HttpPost("enable")]
    public async Task<IActionResult> Enable([FromForm] NotificationSettingsViewModel model, CancellationToken ct = default)
    {
        model.NewOrderSoundEnabled = true;
        return await Post(model, ct);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("disable")]
    public async Task<IActionResult> Disable([FromForm] NotificationSettingsViewModel model, CancellationToken ct = default)
    {
        model.NewOrderSoundEnabled = false;
        return await Post(model, ct);
    }

    private static bool IsValidPercent(int v) => v is 25 or 50 or 75 or 100;

    private static int ToPercent(decimal volume)
    {
        var p = (int)Math.Round(volume * 100m, MidpointRounding.AwayFromZero);
        if (p <= 25) return 25;
        if (p <= 50) return 50;
        if (p <= 75) return 75;
        return 100;
    }

    private IReadOnlyList<NotificationSettingsViewModel.SoundOption> ResolveSounds()
    {
        var soundsDir = Path.Combine(_env.WebRootPath, "sounds");
        var result = new List<NotificationSettingsViewModel.SoundOption>();

        foreach (var name in NotificationSoundOptions.AllowedNames)
        {
            var match = Directory.Exists(soundsDir)
                ? Directory.GetFiles(soundsDir, name + ".*").FirstOrDefault()
                : null;

            var fileName = match is not null ? Path.GetFileName(match) : (name + ".mp3");
            var url = "/sounds/" + fileName;

            result.Add(new NotificationSettingsViewModel.SoundOption
            {
                Name = name,
                Label = NotificationSoundOptions.GetLabel(name),
                Url = url
            });
        }

        return result;
    }
}

