using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Branches;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Branches;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
[Route("branches")]
public sealed class BranchesController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IBranchService _branches;
    private readonly IValidator<CreateBranchCommand> _createValidator;

    public BranchesController(ICurrentTenantService currentTenant, IBranchService branches, IValidator<CreateBranchCommand> createValidator)
    {
        _currentTenant = currentTenant;
        _branches = branches;
        _createValidator = createValidator;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var list = await _branches.GetListAsync(tenant.Id, ct);
        var rows = list.Select(b => new BranchListViewModel.Row
        {
            Id = b.Id,
            Name = b.Name,
            Address = b.Address,
            IsActive = b.IsActive
        }).ToList();

        return View("Index", new BranchListViewModel { Branches = rows });
    }

    [HttpGet("create")]
    public IActionResult Create()
    {
        return View(new CreateBranchViewModel());
    }

    [ValidateAntiForgeryToken]
    [HttpPost("create")]
    public async Task<IActionResult> Create(CreateBranchViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var cmd = new CreateBranchCommand(model.Name, model.Address, model.IsActive);
        var validation = await _createValidator.ValidateAsync(cmd, ct);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors)
                ModelState.AddModelError(string.Empty, e.ErrorMessage);
            return View(model);
        }

        await _branches.CreateAsync(tenant.Id, cmd, ct);

        TempData["Success"] = "Şube oluşturuldu.";
        return RedirectToAction("Index");
    }
}

