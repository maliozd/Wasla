using System.Text.Json;
using System.Text.RegularExpressions;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Version 2 of the message contract between the WebView2 page and the desktop host (WAS-54).
/// Every message is a JSON object <c>{ "version": 2, "type": "...", "payload": { ... } }</c>.
/// The page may only send the commands in <see cref="CommandTypes"/>; the host only sends
/// <see cref="SnapshotUpdated"/>, <see cref="OperationResult"/> and <see cref="HistoryResult"/>.
/// </summary>
public static partial class ShellMessageContract
{
    public const int Version = 2;

    /// <summary>Upper bound for one inbound message, in UTF-16 characters, checked before parsing.</summary>
    public const int MaxInboundMessageLength = 1024;

    public const int MaxPrinterNameLength = 256;
    public const int MaxHistorySearchLength = 64;
    public const int MaxHistoryPage = 1000;

    public const string UiReady = "ui.ready";
    public const string SnapshotRequest = "snapshot.request";
    public const string LanguageChange = "language.change";
    public const string ClassicWindowOpen = "classicWindow.open";
    public const string EngineStart = "engine.start";
    public const string EngineStop = "engine.stop";
    public const string ConnectionTest = "connection.test";
    public const string ConnectionOpenSetup = "connection.openSetup";
    public const string ConnectionReset = "connection.reset";
    public const string PrintersRefresh = "printers.refresh";
    public const string PrinterSave = "printer.save";
    public const string PrinterTestPrint = "printer.testPrint";
    public const string HistoryQuery = "history.query";
    public const string HistoryReprint = "history.reprint";
    public const string LogsOpenFolder = "logs.openFolder";

    public const string SnapshotUpdated = "snapshot.updated";
    public const string OperationResult = "operation.result";
    public const string HistoryResult = "history.result";

    internal enum FieldKind
    {
        RequestId,
        Culture,
        PrinterName,
        HistoryRange,
        HistoryPage,
        HistorySearch,
        HistoryItemRef
    }

    internal sealed record Field(string Name, FieldKind Kind, bool Required);

    internal sealed record CommandSpec(ShellCommandType Type, IReadOnlyList<Field> Fields);

    private static readonly Field RequestIdField = new("requestId", FieldKind.RequestId, Required: true);

    /// <summary>The complete allowlist. A command and its payload fields are accepted only if listed here.</summary>
    internal static readonly IReadOnlyDictionary<string, CommandSpec> Commands =
        new Dictionary<string, CommandSpec>(StringComparer.Ordinal)
        {
            [UiReady] = new(ShellCommandType.UiReady, []),
            [SnapshotRequest] = new(ShellCommandType.SnapshotRequest, []),
            [LanguageChange] = new(ShellCommandType.LanguageChange, [new("culture", FieldKind.Culture, Required: true)]),
            [ClassicWindowOpen] = new(ShellCommandType.ClassicWindowOpen, []),
            [EngineStart] = new(ShellCommandType.EngineStart, [RequestIdField]),
            [EngineStop] = new(ShellCommandType.EngineStop, [RequestIdField]),
            [ConnectionTest] = new(ShellCommandType.ConnectionTest, [RequestIdField]),
            [ConnectionOpenSetup] = new(ShellCommandType.ConnectionOpenSetup, []),
            [ConnectionReset] = new(ShellCommandType.ConnectionReset, [RequestIdField]),
            [PrintersRefresh] = new(ShellCommandType.PrintersRefresh, [RequestIdField]),
            [PrinterSave] = new(ShellCommandType.PrinterSave, [RequestIdField, new("name", FieldKind.PrinterName, Required: true)]),
            [PrinterTestPrint] = new(ShellCommandType.PrinterTestPrint, [RequestIdField]),
            [HistoryQuery] = new(ShellCommandType.HistoryQuery,
            [
                RequestIdField,
                new("range", FieldKind.HistoryRange, Required: true),
                new("page", FieldKind.HistoryPage, Required: true),
                new("search", FieldKind.HistorySearch, Required: false)
            ]),
            [HistoryReprint] = new(ShellCommandType.HistoryReprint, [RequestIdField, new("itemRef", FieldKind.HistoryItemRef, Required: true)]),
            [LogsOpenFolder] = new(ShellCommandType.LogsOpenFolder, [RequestIdField])
        };

    public static readonly IReadOnlyList<string> CommandTypes = Commands.Keys.ToArray();

    public static readonly IReadOnlyDictionary<string, PrintHistoryDateFilter> HistoryRanges =
        new Dictionary<string, PrintHistoryDateFilter>(StringComparer.Ordinal)
        {
            ["today"] = PrintHistoryDateFilter.Today,
            ["last7Days"] = PrintHistoryDateFilter.Last7Days,
            ["last30Days"] = PrintHistoryDateFilter.Last30Days
        };

    // \z rather than $: in .NET, $ also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{7,63}\z")]
    internal static partial Regex RequestIdPattern();

    [GeneratedRegex(@"^h[0-9a-f]{16}\z")]
    internal static partial Regex HistoryItemRefPattern();
}

public enum ShellCommandType
{
    UiReady,
    SnapshotRequest,
    LanguageChange,
    ClassicWindowOpen,
    EngineStart,
    EngineStop,
    ConnectionTest,
    ConnectionOpenSetup,
    ConnectionReset,
    PrintersRefresh,
    PrinterSave,
    PrinterTestPrint,
    HistoryQuery,
    HistoryReprint,
    LogsOpenFolder
}

/// <summary>A validated command from the page. Only the fields its type allows are ever set.</summary>
public sealed record ShellCommand(
    ShellCommandType Type,
    string? RequestId = null,
    string? Culture = null,
    string? PrinterName = null,
    PrintHistoryDateFilter? HistoryRange = null,
    int? HistoryPage = null,
    string? HistorySearch = null,
    string? HistoryItemRef = null);

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
/// Strict parser for page-to-host messages. It never dispatches anything itself and never maps a string from the
/// page to a method name: the only possible results are the fixed <see cref="ShellCommandType"/> values from the
/// allowlist. Unknown properties, versions, types and payload fields, and out-of-range values, are rejected.
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

            if (type is not { ValueKind: JsonValueKind.String } t
                || !ShellMessageContract.Commands.TryGetValue(t.GetString()!, out var spec))
            {
                return Reject(ShellMessageRejection.UnknownType, out rejection);
            }

            if (payload is { } p && p.ValueKind != JsonValueKind.Object)
                return Reject(ShellMessageRejection.InvalidPayload, out rejection);

            if (!TryReadPayload(spec, payload, out command))
                return Reject(ShellMessageRejection.InvalidPayload, out rejection);
        }

        rejection = ShellMessageRejection.None;
        return true;
    }

    private static bool TryReadPayload(ShellMessageContract.CommandSpec spec, JsonElement? payload, out ShellCommand? command)
    {
        command = null;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (payload is { } p)
        {
            foreach (var property in p.EnumerateObject())
            {
                if (!spec.Fields.Any(f => f.Name == property.Name) || !values.TryAdd(property.Name, property.Value))
                    return false;
            }
        }

        var result = new ShellCommand(spec.Type);
        foreach (var field in spec.Fields)
        {
            if (!values.TryGetValue(field.Name, out var value))
            {
                if (field.Required)
                    return false;
                continue;
            }

            if (!TryApply(field.Kind, value, ref result))
                return false;
        }

        command = result;
        return true;
    }

    private static bool TryApply(ShellMessageContract.FieldKind kind, JsonElement value, ref ShellCommand command)
    {
        switch (kind)
        {
            case ShellMessageContract.FieldKind.RequestId:
                if (!TryString(value, out var requestId) || !ShellMessageContract.RequestIdPattern().IsMatch(requestId))
                    return false;
                command = command with { RequestId = requestId };
                return true;

            case ShellMessageContract.FieldKind.Culture:
                if (!TryString(value, out var culture) || !SupportedCultures.IsExactSupportedName(culture))
                    return false;
                command = command with { Culture = culture };
                return true;

            case ShellMessageContract.FieldKind.PrinterName:
                if (!TryString(value, out var name)
                    || name.Length is 0 or > ShellMessageContract.MaxPrinterNameLength
                    || name.Trim().Length != name.Length
                    || name.Any(char.IsControl))
                {
                    return false;
                }
                command = command with { PrinterName = name };
                return true;

            case ShellMessageContract.FieldKind.HistoryRange:
                if (!TryString(value, out var range) || !ShellMessageContract.HistoryRanges.TryGetValue(range, out var filter))
                    return false;
                command = command with { HistoryRange = filter };
                return true;

            case ShellMessageContract.FieldKind.HistoryPage:
                if (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetInt32(out var page)
                    || page < 0
                    || page > ShellMessageContract.MaxHistoryPage)
                {
                    return false;
                }
                command = command with { HistoryPage = page };
                return true;

            case ShellMessageContract.FieldKind.HistorySearch:
                if (!TryString(value, out var search)
                    || search.Length > ShellMessageContract.MaxHistorySearchLength
                    || search.Any(char.IsControl))
                {
                    return false;
                }
                command = command with { HistorySearch = search };
                return true;

            case ShellMessageContract.FieldKind.HistoryItemRef:
                if (!TryString(value, out var itemRef) || !ShellMessageContract.HistoryItemRefPattern().IsMatch(itemRef))
                    return false;
                command = command with { HistoryItemRef = itemRef };
                return true;

            default:
                return false;
        }
    }

    private static bool TryString(JsonElement value, out string text)
    {
        text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        return value.ValueKind == JsonValueKind.String;
    }

    private static bool Reject(ShellMessageRejection reason, out ShellMessageRejection rejection)
    {
        rejection = reason;
        return false;
    }
}
