using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Everything the status page may show, already localized and formatted by the host. The page renders it
/// verbatim and never derives state of its own. It intentionally has no field for the server URL, device
/// token, installation id or any other credential or tenant identifier.
/// </summary>
public sealed record ShellSnapshot(
    string Culture,
    string Direction,
    ShellConnectionView Connection,
    ShellDeviceView Device,
    ShellPrinterView Printer,
    ShellActivityView Activity,
    ShellJobView? LastJob,
    bool DryRun,
    string VersionLabel,
    IReadOnlyList<ShellLanguageOption> Languages,
    IReadOnlyDictionary<string, string> Strings);

/// <param name="State">One of <see cref="ShellConnectionStates"/>.</param>
public sealed record ShellConnectionView(string State, string Label, string Detail);

public sealed record ShellDeviceView(string Name);

/// <param name="State"><c>ready</c>, <c>notConfigured</c>, <c>notFound</c> or <c>dryRun</c>.</param>
public sealed record ShellPrinterView(string? Name, string State, string Label);

/// <param name="LastContact">Formatted time of the last successful server contact, or null.</param>
/// <param name="LastPrint">Formatted time of the last successful print, or null.</param>
public sealed record ShellActivityView(string? LastContact, string? LastPrint, int JobsToday, int FailedToday);

/// <param name="Status"><c>pending</c>, <c>printing</c>, <c>printed</c>, <c>failed</c> or <c>skipped</c>.</param>
public sealed record ShellJobView(string Status, string StatusLabel, string Order, string TypeLabel, string Time);

public sealed record ShellLanguageOption(string Culture, string NativeName);

public static class ShellConnectionStates
{
    public const string Online = "online";
    public const string Connecting = "connecting";
    public const string Offline = "offline";
    public const string Error = "error";
    public const string NotConfigured = "notConfigured";
    public const string Stopped = "stopped";

    public static readonly IReadOnlyList<string> All = [Online, Connecting, Offline, Error, NotConfigured, Stopped];
}

/// <summary>Host-to-page envelope. <see cref="Sequence"/> increases with every send so the page can drop stale updates.</summary>
public sealed record ShellSnapshotMessage(int Version, string Type, long Sequence, ShellSnapshot Payload);

public static class ShellMessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static string SerializePayload(ShellSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    public static string SerializeSnapshotMessage(ShellSnapshot snapshot, long sequence) =>
        JsonSerializer.Serialize(
            new ShellSnapshotMessage(ShellMessageContract.Version, ShellMessageContract.SnapshotUpdated, sequence, snapshot),
            Options);
}
