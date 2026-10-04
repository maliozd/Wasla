using System.Text.Json;
using Wasla.PrintBridge.Localization;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Version 1 of the message contract between the WebView2 status page and the desktop host.
/// Every message is a JSON object <c>{ "version": 1, "type": "...", "payload": { ... } }</c>.
/// The page may only send the commands listed here; the host only sends <see cref="SnapshotUpdated"/>.
/// </summary>
public static class ShellMessageContract
{
    public const int Version = 1;

    /// <summary>Upper bound for one inbound message, in UTF-16 characters, checked before parsing.</summary>
    public const int MaxInboundMessageLength = 1024;

    public const string UiReady = "ui.ready";
    public const string SnapshotRequest = "snapshot.request";
    public const string LanguageChange = "language.change";
    public const string ClassicWindowOpen = "classicWindow.open";

    public const string SnapshotUpdated = "snapshot.updated";

    public static readonly IReadOnlyList<string> CommandTypes =
    [
        UiReady,
        SnapshotRequest,
        LanguageChange,
        ClassicWindowOpen
    ];
}

public enum ShellCommandType
{
    UiReady,
    SnapshotRequest,
    LanguageChange,
    ClassicWindowOpen
}

/// <summary>A validated command from the page. <see cref="Culture"/> is set only for language changes.</summary>
public sealed record ShellCommand(ShellCommandType Type, string? Culture = null);

public enum ShellMessageRejection
{
    None,
    Empty,
    TooLarge,
    MalformedJson,
    NotAnObject,
    UnexpectedProperty,
    DuplicateProperty,
    UnsupportedVersion,
    UnknownType,
    InvalidPayload,
    UntrustedSource
}

/// <summary>
/// Strict parser for page-to-host messages. It never dispatches anything itself and never maps a
/// string from the page to a method name: the only possible results are the fixed <see cref="ShellCommandType"/>
/// values. Unknown properties, versions, types and payload fields are rejected.
/// </summary>
public static class ShellMessageParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 4,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    public static bool TryParse(string? raw, out ShellCommand? command, out ShellMessageRejection rejection)
    {
        command = null;

        if (string.IsNullOrEmpty(raw))
            return Reject(ShellMessageRejection.Empty, out rejection);

        if (raw.Length > ShellMessageContract.MaxInboundMessageLength)
            return Reject(ShellMessageRejection.TooLarge, out rejection);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw, DocumentOptions);
        }
        catch (JsonException)
        {
            return Reject(ShellMessageRejection.MalformedJson, out rejection);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reject(ShellMessageRejection.NotAnObject, out rejection);

            JsonElement? version = null;
            JsonElement? type = null;
            JsonElement? payload = null;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "version" when version is null:
                        version = property.Value;
                        break;
                    case "type" when type is null:
                        type = property.Value;
                        break;
                    case "payload" when payload is null:
                        payload = property.Value;
                        break;
                    case "version" or "type" or "payload":
                        return Reject(ShellMessageRejection.DuplicateProperty, out rejection);
                    default:
                        return Reject(ShellMessageRejection.UnexpectedProperty, out rejection);
                }
            }

            if (version is not { ValueKind: JsonValueKind.Number } v
                || !v.TryGetInt32(out var versionNumber)
                || versionNumber != ShellMessageContract.Version)
            {
                return Reject(ShellMessageRejection.UnsupportedVersion, out rejection);
            }

            if (type is not { ValueKind: JsonValueKind.String } t)
                return Reject(ShellMessageRejection.UnknownType, out rejection);

            var commandType = t.GetString() switch
            {
                ShellMessageContract.UiReady => ShellCommandType.UiReady,
                ShellMessageContract.SnapshotRequest => ShellCommandType.SnapshotRequest,
                ShellMessageContract.LanguageChange => ShellCommandType.LanguageChange,
                ShellMessageContract.ClassicWindowOpen => ShellCommandType.ClassicWindowOpen,
                _ => (ShellCommandType?)null
            };

            if (commandType is null)
                return Reject(ShellMessageRejection.UnknownType, out rejection);

            if (payload is { } p && p.ValueKind != JsonValueKind.Object)
                return Reject(ShellMessageRejection.InvalidPayload, out rejection);

            if (commandType == ShellCommandType.LanguageChange)
            {
                if (!TryReadCulture(payload, out var culture))
                    return Reject(ShellMessageRejection.InvalidPayload, out rejection);

                command = new ShellCommand(ShellCommandType.LanguageChange, culture);
            }
            else
            {
                if (payload is { } empty && empty.EnumerateObject().Any())
                    return Reject(ShellMessageRejection.InvalidPayload, out rejection);

                command = new ShellCommand(commandType.Value);
            }
        }

        rejection = ShellMessageRejection.None;
        return true;
    }

    private static bool TryReadCulture(JsonElement? payload, out string culture)
    {
        culture = string.Empty;
        if (payload is not { } p)
            return false;

        string? value = null;
        foreach (var property in p.EnumerateObject())
        {
            if (property.Name != "culture" || value is not null || property.Value.ValueKind != JsonValueKind.String)
                return false;

            value = property.Value.GetString();
        }

        if (!SupportedCultures.IsExactSupportedName(value))
            return false;

        culture = value!;
        return true;
    }

    private static bool Reject(ShellMessageRejection reason, out ShellMessageRejection rejection)
    {
        rejection = reason;
        return false;
    }
}
