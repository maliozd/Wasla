namespace Wasla.Web.Models.Ui;

public sealed class WaslaPageHeaderModel
{
    public string? Eyebrow { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? ActionUrl { get; init; }

    public string? ActionText { get; init; }

    public string? ActionIconClass { get; init; }
}
