using System.Text.Json.Serialization;

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

    /// <summary>
    /// Desktop UI: unset or <c>WinForms</c> keeps the classic window; <c>WebView2</c> opts in to the WebView2
    /// app. Not written to settings unless it was set, so existing configurations are saved unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Shell { get; set; }

    /// <summary>A separate copy to change and then publish; every property is a value.</summary>
    public UiOptions Clone() => (UiOptions)MemberwiseClone();
}
