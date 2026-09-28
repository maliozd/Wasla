namespace Wasla.Web.Models.Dashboard;

public sealed class TenantSetupPanelViewModel
{
    public bool IsReady { get; init; }
    public int RequiredCompleted { get; init; }
    public int RequiredTotal { get; init; }
    public int ProgressPercent { get; init; }
    public string RestaurantName { get; init; } = string.Empty;
    public Guid TenantId { get; init; }
    public bool ShowAddTeamMember { get; init; }
    public IReadOnlyList<TenantSetupStepViewModel> Steps { get; init; } = Array.Empty<TenantSetupStepViewModel>();
}

public sealed class TenantSetupStepViewModel
{
    public string Title { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public string? Hint { get; init; }
    public bool IsComplete { get; init; }
    public bool IsOptional { get; init; }
    public bool IsUnavailable { get; init; }
    public bool ShowsCompletionMark { get; init; } = true;
    public string ActionText { get; init; } = string.Empty;
    public string ActionHref { get; init; } = string.Empty;
    public bool OpenInNewTab { get; init; }
    public string StatusText { get; init; } = string.Empty;
}
