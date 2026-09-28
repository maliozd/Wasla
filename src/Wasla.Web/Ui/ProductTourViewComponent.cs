using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Abstractions.Tours;
using Wasla.Application.Tours;
using Wasla.Domain.Enums;
using Wasla.Web.Models.Tours;

namespace Wasla.Web.Ui;

public sealed class ProductTourViewComponent : ViewComponent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ICurrentTenantService _currentTenant;
    private readonly IUserProductTourService _tours;
    private readonly IAntiforgery _antiforgery;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<ProductTourViewComponent> _logger;

    public ProductTourViewComponent(
        ICurrentTenantService currentTenant,
        IUserProductTourService tours,
        IAntiforgery antiforgery,
        IStringLocalizer<SharedResource> localizer,
        ILogger<ProductTourViewComponent> logger)
    {
        _currentTenant = currentTenant;
        _tours = tours;
        _antiforgery = antiforgery;
        _localizer = localizer;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync(
        string tourKey,
        bool suppressAutoStart = false,
        string? waitUntilGone = null,
        string? finishUrl = null,
        string? resumeWhenPresent = null,
        string? skipConfirmModal = null)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null || !TryGetUser(out var userId, out var role))
            return Content(string.Empty);

        var steps = ProductTourCatalog.StepsFor(tourKey, role);
        if (steps.Count == 0)
            return Content(string.Empty);

        var completed = false;
        try
        {
            completed = await _tours.IsCompletedAsync(tenant.Id, userId, tourKey, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Product tour status failed: {ExceptionType}", ex.GetType().Name);
        }

        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        var config = new
        {
            key = tourKey,
            autoStart = !suppressAutoStart && !completed,
            completeUrl = "/product-tours/complete",
            finishUrl,
            token = tokens.RequestToken,
            waitUntilGone,
            resumeWhenPresent,
            skipConfirmModal,
            copy = new
            {
                skip = _localizer["Tour.Skip"].Value,
                back = _localizer["Tour.Back"].Value,
                continueLabel = _localizer["Tour.Continue"].Value,
                finish = _localizer["Tour.Finish"].Value,
                progress = _localizer["Tour.Progress"].Value,
                close = _localizer["Tour.Close"].Value,
                realOrderPaused = _localizer["Tour.RealOrderPaused"].Value,
                viewRealOrder = _localizer["Tour.ViewRealOrder"].Value,
                continueTraining = _localizer["Tour.ContinueTraining"].Value,
                skipTraining = _localizer["Demo.SkipTraining"].Value,
                trainingPaused = _localizer["Tour.TrainingPaused"].Value,
                selectDemo = _localizer["Demo.SelectTitle"].Value,
                selectDemoBody = _localizer["Demo.SelectBody"].Value
            },
            steps = steps.Select(step => new
            {
                target = step.Target,
                placement = step.Placement,
                title = _localizer[step.TitleKey].Value,
                body = _localizer[step.BodyKey, step.BodyArguments ?? []].Value,
                advance = step.Advance,
                eventName = step.EventName,
                eventMatch = step.EventAction is null ? null : new { action = step.EventAction, demo = true },
                readySelector = step.ReadySelector,
                primaryAction = step.PrimaryAction,
                primaryLabel = step.PrimaryLabelKey is null ? null : _localizer[step.PrimaryLabelKey].Value,
                completeSelector = step.CompleteSelector,
                resumeSelector = step.ResumeSelector
            })
        };

        return View(new ProductTourViewModel
        {
            ConfigJson = JsonSerializer.Serialize(config, JsonOptions),
            ReplayLabel = _localizer["Tour.Replay"].Value
        });
    }

    private bool TryGetUser(out Guid userId, out UserRole role)
    {
        userId = Guid.Empty;
        role = default;
        var user = UserClaimsPrincipal;
        var userIdValue = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("UserId");
        var roleValue = user.FindFirstValue(ClaimTypes.Role) ?? user.FindFirstValue("Role");
        return Guid.TryParse(userIdValue, out userId)
            && Enum.TryParse(roleValue, ignoreCase: true, out role);
    }
}
