using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FluentValidation;
using OrderHub.Application.Abstractions.Branches;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Web.Models.Branches;

namespace OrderHub.Web.Controllers;

[Authorize]
[Route("branches")]
public sealed class BranchesController : BaseController
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IBranchService _branches;
    private readonly IValidator<CreateBranchCommand> _createValidator;

    public BranchesController(ICurrentCustomerService currentCustomer, IBranchService branches, IValidator<CreateBranchCommand> createValidator)
    {
        _currentCustomer = currentCustomer;
        _branches = branches;
        _createValidator = createValidator;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var list = await _branches.GetListAsync(customer.Id, ct);
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

        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var cmd = new CreateBranchCommand(model.Name, model.Address, model.IsActive);
        var validation = await _createValidator.ValidateAsync(cmd, ct);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors)
                ModelState.AddModelError(string.Empty, e.ErrorMessage);
            return View(model);
        }

        await _branches.CreateAsync(
            customer.Id,
            cmd,
            ct);

        TempData["Success"] = "Şube oluşturuldu.";
        return RedirectToAction("Index");
    }
}

