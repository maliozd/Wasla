using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wasla.PrintBridge.Ipc;

/// <summary>
/// The local pipe protocol between the Print Bridge app and the Print Bridge service (WAS-61). Every message is a frame:
/// a 4-byte little-endian length, then that many bytes of UTF-8 JSON. See
/// <c>docs/integrations/print-bridge-service.md</c> for the contract.
/// </summary>
public static partial class PrintBridgeIpcProtocol
{
    /// <summary>The protocol this build speaks. A change to any message's meaning raises it.</summary>
    public const int Version = 1;

    /// <summary>The oldest and newest versions this service accepts (only <see cref="Version"/> for now).</summary>
    public const int MinSupportedVersion = 1;
    public const int MaxSupportedVersion = 1;

    public const int MaxClientFrameBytes = 16 * 1024;
    public const int MaxServiceFrameBytes = 1024 * 1024;
    public const int MaxInFlightRequestsPerClient = 8;
    public const int MaxServerInstances = 8;

    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    // Message types.
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Error = "error";
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";

    // Commands.
    public const string StateGet = "state.get";
    public const string EngineStart = "engine.start";
    public const string EngineStop = "engine.stop";
    public const string ConnectionTest = "connection.test";
    public const string ConnectionCheck = "connection.check";
    public const string ConnectionApply = "connection.apply";
    public const string ConnectionReset = "connection.reset";
    public const string PrinterTest = "printer.test";
    public const string PrintersList = "printers.list";
    public const string PrinterSave = "printer.save";
    public const string SettingsSave = "settings.save";
    public const string LanguageSave = "language.save";
    public const string HistoryQuery = "history.query";
    public const string HistoryReprint = "history.reprint";
    public const string HistoryResolve = "history.resolve";
    public const string SetupApply = "setup.apply";
    public const string SetupConsentAnswer = "setup.consentAnswer";

    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        StateGet, EngineStart, EngineStop, ConnectionTest, ConnectionCheck, ConnectionApply, ConnectionReset, PrinterTest,
        PrintersList, PrinterSave, SettingsSave, LanguageSave, HistoryQuery, HistoryReprint, HistoryResolve, SetupApply,
        SetupConsentAnswer
    };

    // Events.
    public const string StateEvent = "state";
    public const string SetupConsentEvent = "setup.consent";
    public const string SetupBackgroundEvent = "setup.background";

    // Error codes.
    public const string ErrorIncompatibleProtocol = "incompatible_protocol";
    public const string ErrorMalformed = "malformed";
    public const string ErrorUnknownCommand = "unknown_command";
    public const string ErrorBusy = "busy";
    public const string ErrorNotActive = "not_active";
    public const string ErrorLocalized = "localized";
    public const string ErrorConnection = "connection";
    public const string ErrorCancelled = "cancelled";
    public const string ErrorConsentNotFound = "consent_not_found";
    public const string ErrorFailed = "failed";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        MaxDepth = 16
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{7,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex RequestIdPattern();

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    public static partial Regex OpaqueIdPattern();

    /// <summary>Writes one frame. The caller serializes writes to a stream.</summary>
    public static async Task WriteFrameAsync(Stream stream, byte[] utf8Json, int maxBytes, CancellationToken ct)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > maxBytes)
            throw new PrintBridgeIpcProtocolException("frame_size");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, utf8Json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(utf8Json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame and returns its text, or null when the other side closed the pipe between frames. A length out of
    /// range, a frame cut short or bytes that are not UTF-8 throw <see cref="PrintBridgeIpcProtocolException"/>; the
    /// connection must then be closed, because the stream is no longer at a frame boundary.
    /// </summary>
    public static async Task<string?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var header = new byte[4];
        var read = await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0)
            return null;
        if (read < header.Length)
            throw new PrintBridgeIpcProtocolException("frame_truncated");

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxBytes)
            throw new PrintBridgeIpcProtocolException("frame_size");

        var body = new byte[length];
        if (await ReadExactlyOrEofAsync(stream, body, ct).ConfigureAwait(false) < length)
            throw new PrintBridgeIpcProtocolException("frame_truncated");

        try
        {
            return StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            throw new PrintBridgeIpcProtocolException("frame_encoding");
        }
    }

    public static byte[] Serialize<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

    /// <summary>
    /// Parses JSON that must be an object without duplicate property names at any depth. System.Text.Json keeps the last
    /// of duplicate names, which would let one message mean two things to two readers.
    /// </summary>
    public static JsonDocument ParseStrict(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false });
        }
        catch (JsonException)
        {
            throw new PrintBridgeIpcProtocolException(ErrorMalformed);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateNames(document.RootElement))
        {
            document.Dispose();
            throw new PrintBridgeIpcProtocolException(ErrorMalformed);
        }

        return document;
    }

    /// <summary>Deserializes <paramref name="element"/> strictly: unknown members and type mismatches are malformed.</summary>
    public static T Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(JsonOptions) ?? throw new PrintBridgeIpcProtocolException(ErrorMalformed);
        }
        catch (JsonException)
        {
            throw new PrintBridgeIpcProtocolException(ErrorMalformed);
        }
        catch (NotSupportedException)
        {
            throw new PrintBridgeIpcProtocolException(ErrorMalformed);
        }
    }

    private static bool HasDuplicateNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || HasDuplicateNames(property.Value))
                        return true;
                }

                return false;
            }
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(HasDuplicateNames);
            default:
                return false;
        }
    }

    private static async Task<int> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                return total;
            total += read;
        }

        return total;
    }
}

/// <summary>A frame or message that breaks the protocol. The message is a fixed code; it never contains received content.</summary>
public sealed class PrintBridgeIpcProtocolException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
