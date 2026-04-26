using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.PlatformConnections;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Web.Controllers;
using OrderHub.Web.Models.PlatformConnections;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
[Route("platform-connections")]
public sealed class PlatformConnectionsController : BaseController
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IPlatformConnectionService _connections;
    private readonly IValidator<CreatePlatformConnectionCommand> _createValidator;

    public PlatformConnectionsController(
        ICurrentCustomerService currentCustomer,
        IPlatformConnectionService connections,
        IValidator<CreatePlatformConnectionCommand> createValidator)
    {
        _currentCustomer = currentCustomer;
        _connections = connections;
        _createValidator = createValidator;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var list = await _connections.GetListAsync(customer.Id, ct);
        var rows = list.Select(x => new PlatformConnectionListViewModel.Row
        {
            Id = x.Id,
            Platform = x.Platform,
            StoreId = x.StoreId,
            IsActive = x.IsActive,
            ConsecutiveFailures = x.ConsecutiveFailures,
            CircuitOpenUntilUtc = x.CircuitOpenUntilUtc,
            LastSuccessfulSyncUtc = x.LastSuccessfulSyncUtc
        }).ToList();

        return View("Index", new PlatformConnectionListViewModel { Connections = rows });
    }

    [HttpGet("create")]
    public IActionResult Create()
    {
        if (!IsOwnerOrManager()) return Forbid();
        return View(new CreatePlatformConnectionViewModel());
    }

    [ValidateAntiForgeryToken]
    [HttpPost("create")]
    public async Task<IActionResult> Create(CreatePlatformConnectionViewModel model, CancellationToken ct)
    {
        if (!IsOwnerOrManager()) return Forbid();
        if (!ModelState.IsValid) return View(model);

        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var cmd = new CreatePlatformConnectionCommand(
            model.Platform,
            model.StoreId,
            model.ApiKey,
            model.ApiSecret,
            model.IsActive);

        var validation = await _createValidator.ValidateAsync(cmd, ct);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors)
                ModelState.AddModelError(string.Empty, e.ErrorMessage);
            return View(model);
        }

        var result = await _connections.CreateAsync(customer.Id, cmd, ct);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "İşlem başarısız.");
            return View(model);
        }

        TempData["Success"] = "Platform bağlantısı oluşturuldu.";
        return RedirectToAction("Index");
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        if (!IsOwnerOrManager()) return Forbid();
        return await SetActive(id, true, ct);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        if (!IsOwnerOrManager()) return Forbid();
        return await SetActive(id, false, ct);
    }

    private async Task<IActionResult> SetActive(Guid id, bool isActive, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var ok = await _connections.SetActiveAsync(customer.Id, id, isActive, ct);
        if (!ok) return NotFound();

        TempData["Success"] = isActive ? "Bağlantı aktif edildi." : "Bağlantı pasif edildi.";
        return RedirectToAction("Index");
    }
}

