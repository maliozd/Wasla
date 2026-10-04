namespace Wasla.PrintBridge.Options;

public sealed class UiOptions
{
    public const string SectionName = "Ui";

    public string Language { get; set; } = string.Empty;

    public bool StartWithWindows { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    public int? WindowWidth { get; set; }

    public int? WindowHeight { get; set; }

    public int? WindowLeft { get; set; }

    public int? WindowTop { get; set; }

    public string? WindowState { get; set; }
}
