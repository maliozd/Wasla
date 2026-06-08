namespace OrderHub.PrintBridge.Options;

public sealed class UiOptions
{
    public const string SectionName = "Ui";

    public string Language { get; set; } = string.Empty;

    public bool StartWithWindows { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;
}
