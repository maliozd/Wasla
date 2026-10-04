using System.Collections;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Wasla.Infrastructure.Diagnostics;

/// <summary>
/// Per-request structured log scope. TenantId is added after tenant resolution
/// and is visible to loggers that enumerate the scope when the event is written.
/// </summary>
public sealed class RequestLogState : IReadOnlyList<KeyValuePair<string, object>>
{
    public const string ItemKey = "Wasla.RequestLogState";

    private readonly List<KeyValuePair<string, object>> _items = new(2);

    private RequestLogState(string traceId)
    {
        TraceId = traceId;
        _items.Add(new KeyValuePair<string, object>(nameof(TraceId), traceId));
    }

    public string TraceId { get; }

    public string? TenantId { get; private set; }

    public static RequestLogState Capture(HttpContext httpContext)
    {
        var activityTraceId = Activity.Current?.TraceId.ToString();
        var raw = string.IsNullOrEmpty(activityTraceId) || activityTraceId == "00000000000000000000000000000000"
            ? httpContext.TraceIdentifier
            : activityTraceId;
        return new RequestLogState(SanitizeTraceToken(raw));
    }

    public void SetTenantId(Guid tenantId)
    {
        var value = tenantId.ToString("D");
        TenantId = value;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Key == nameof(TenantId))
            {
                _items[i] = new KeyValuePair<string, object>(nameof(TenantId), value);
                return;
            }
        }

        _items.Add(new KeyValuePair<string, object>(nameof(TenantId), value));
    }

    public int Count => _items.Count;

    public KeyValuePair<string, object> this[int index] => _items[index];

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static string SanitizeTraceToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > 128)
            return "unknown";

        foreach (var ch in trimmed)
        {
            var allowed = ch is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')
                or (>= 'g' and <= 'z') or (>= 'G' and <= 'Z')
                or '-' or '_' or '.';
            if (!allowed)
                return "unknown";
        }

        return trimmed;
    }
}
