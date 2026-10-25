using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Web.Controllers;
using Wasla.Web.Models.TenantUsers;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageTenantUsers)]
[Route("settings/users")]
public sealed class TenantUsersController : BaseController
{
    private static readonly UserRole[] AllowedRoles =
    [
        UserRole.Owner,
        UserRole.Manager,
        UserRole.Kitchen,
        UserRole.Cashier,
        UserRole.Viewer
    ];

    private readonly ICurrentTenantService _currentTenant;
    private readonly ITenantUserRoleService _users;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public TenantUsersController(
        ICurrentTenantService currentTenant,
        ITenantUserRoleService users,
        IStringLocalizer<SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _users = users;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var users = await _users.ListUsersAsync(tenant.Id, ct).ConfigureAwait(false);
        var rows = users.Select(u => new TenantUserRowViewModel
        {
            Id = u.Id,
            Email = u.Email,
            FullName = u.FullName,
            Role = u.Role,
            IsActive = u.IsActive,
            CreatedAt = u.CreatedAt,
            LastLoginAt = u.LastLoginAt
        }).ToList();

        return View(new TenantUsersViewModel
        {
            Users = rows,
            RoleOptions = TenantUserRoleOptions.All,
            TotalCount = rows.Count,
            ActiveCount = rows.Count(u => u.IsActive),
            RoleCounts = TenantUserRoleOptions.All
                .Select(role => new TenantUserRoleCountViewModel
                {
                    Role = role,
                    Count = rows.Count(u => u.Role == role)
                })
                .ToList()
        });
    }

    [HttpGet("create")]
    public IActionResult Create()
    {
        if (_currentTenant.CurrentTenant is null)
            return NotFound();

        return View(new TenantUserCreateViewModel());
    }

    [ValidateAntiForgeryToken]
    [HttpPost("create")]
    public async Task<IActionResult> Create(TenantUserCreateViewModel model, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        if (!TryParseRole(model.Role, out var parsedRole))
            ModelState.AddModelError(nameof(model.Role), _localizer["TenantUsers.InvalidRole"].Value);
        if (!string.Equals(model.Password, model.ConfirmPassword, StringComparison.Ordinal))
            ModelState.AddModelError(nameof(model.ConfirmPassword), _localizer["Validation.PasswordMismatch"].Value);

        if (!ModelState.IsValid)
            return View(model);

        if (!TryGetActor(out var actor))
            return Forbid();

        var result = await _users.CreateUserAsync(
            tenant.Id,
            actor,
            new TenantUserCreateCommand(
                model.Email,
                model.FullName,
                parsedRole,
                model.IsActive,
                model.Password),
            ct).ConfigureAwait(false);

        if (result.Outcome == TenantUserRoleUpdateOutcome.ActorNotAuthorized)
            return Forbid();

        if (!result.Succeeded)
        {
            AddMutationErrors(result, nameof(model.Password));
            return View(model);
        }

        TempData["TenantUsersMessage"] = _localizer["TenantUsers.UserCreated"].Value;
        return RedirectToAction(nameof(Details), new { id = result.UserId });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var user = await _users.GetUserAsync(tenant.Id, id, ct).ConfigureAwait(false);
        if (user is null)
            return NotFound();

        return View(ToDetailsViewModel(user));
    }

    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var user = await _users.GetUserAsync(tenant.Id, id, ct).ConfigureAwait(false);
        if (user is null)
            return NotFound();

        return View(new TenantUserEditViewModel
        {
            Id = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.Role.ToString(),
            IsActive = user.IsActive,
            RoleOptions = TenantUserRoleOptions.All
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, TenantUserEditViewModel model, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        if (id != model.Id)
            return NotFound();

        if (!TryParseRole(model.Role, out var parsedRole))
            ModelState.AddModelError(nameof(model.Role), _localizer["TenantUsers.InvalidRole"].Value);
        if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
            ModelState.AddModelError(nameof(model.ConfirmPassword), _localizer["Validation.PasswordMismatch"].Value);

        if (!ModelState.IsValid)
            return View(model);

        if (!TryGetActor(out var actor))
            return Forbid();

        var result = await _users.UpdateUserAsync(
            tenant.Id,
            actor,
            id,
            new TenantUserUpdateCommand(
                model.FullName,
                parsedRole,
                model.IsActive,
                string.IsNullOrWhiteSpace(model.NewPassword) ? null : model.NewPassword),
            ct).ConfigureAwait(false);

        if (result.Outcome == TenantUserRoleUpdateOutcome.ActorNotAuthorized)
            return Forbid();

        if (!result.Succeeded)
        {
            AddMutationErrors(result, nameof(model.NewPassword));
            return View(model);
        }

        TempData["TenantUsersMessage"] = _localizer["TenantUsers.UserUpdated"].Value;
        return RedirectToAction(nameof(Details), new { id });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        if (!TryGetActor(out var actor))
            return Forbid();

        var result = await _users.SetActiveAsync(tenant.Id, actor, id, isActive: true, ct).ConfigureAwait(false);
        return HandleUpdateResult(result, "TenantUsers.UserActivated", id);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        if (!TryGetActor(out var actor))
            return Forbid();

        var result = await _users.SetActiveAsync(tenant.Id, actor, id, isActive: false, ct).ConfigureAwait(false);
        return HandleUpdateResult(result, "TenantUsers.UserDeactivated", id);
    }

    /// <summary>
    /// The signed-in Owner, from the session that was validated for this request. The service re-checks it in the same
    /// transaction as the change.
    /// </summary>
    private bool TryGetActor(out TenantUserActor actor)
    {
        actor = null!;
        if (!TenantSessionClaims.TryRead(User, out var session))
            return false;

        actor = session.ToActor();
        return true;
    }

    private IActionResult HandleUpdateResult(TenantUserRoleUpdateResult result, string successKey, Guid id)
    {
        if (result.Outcome == TenantUserRoleUpdateOutcome.ActorNotAuthorized)
            return Forbid();

        TempData[result.Succeeded ? "TenantUsersMessage" : "TenantUsersError"] =
            _localizer[MessageKey(result, successKey)].Value;

        return result.Outcome == TenantUserRoleUpdateOutcome.UserNotFound
            ? RedirectToAction(nameof(Index))
            : RedirectToAction(nameof(Details), new { id });
    }

    private static string MessageKey(TenantUserRoleUpdateResult result, string successKey) =>
        result.Outcome switch
        {
            TenantUserRoleUpdateOutcome.Updated => successKey,
            TenantUserRoleUpdateOutcome.UserNotFound => "TenantUsers.UserNotFound",
            TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved => "TenantUsers.LastOwnerBlocked",
            TenantUserRoleUpdateOutcome.InvalidRole => "TenantUsers.InvalidRole",
            TenantUserRoleUpdateOutcome.SelfChangeNotAllowed => "TenantUsers.SelfChangeBlocked",
            _ => "TenantUsers.UpdateFailed"
        };

    private void AddMutationErrors(TenantUserMutationResult result, string passwordField)
    {
        switch (result.Outcome)
        {
            case TenantUserRoleUpdateOutcome.UserNotFound:
                ModelState.AddModelError(string.Empty, _localizer["TenantUsers.UserNotFound"].Value);
                break;
            case TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved:
                ModelState.AddModelError(string.Empty, _localizer["TenantUsers.LastOwnerBlocked"].Value);
                break;
            case TenantUserRoleUpdateOutcome.InvalidRole:
                ModelState.AddModelError("Role", _localizer["TenantUsers.InvalidRole"].Value);
                break;
            case TenantUserRoleUpdateOutcome.DuplicateEmail:
                ModelState.AddModelError("Email", _localizer["TenantUsers.EmailAlreadyExists"].Value);
                break;
            case TenantUserRoleUpdateOutcome.SelfChangeNotAllowed:
                ModelState.AddModelError(string.Empty, _localizer["TenantUsers.SelfChangeBlocked"].Value);
                break;
            case TenantUserRoleUpdateOutcome.InvalidPassword:
                foreach (var error in result.PasswordErrors)
                    ModelState.AddModelError(passwordField, _localizer[error].Value);
                break;
            default:
                ModelState.AddModelError(string.Empty, _localizer["TenantUsers.UpdateFailed"].Value);
                break;
        }
    }

    private static bool TryParseRole(string role, out UserRole parsedRole)
    {
        if (!Enum.TryParse(role, ignoreCase: true, out parsedRole))
            return false;

        return AllowedRoles.Contains(parsedRole);
    }

    private static TenantUserDetailsViewModel ToDetailsViewModel(TenantUserSummaryDto user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FullName = user.FullName,
        Role = user.Role,
        IsActive = user.IsActive,
        CreatedAt = user.CreatedAt,
        LastLoginAt = user.LastLoginAt
    };
}
