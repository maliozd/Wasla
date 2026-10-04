using Microsoft.AspNetCore.Http;

namespace Wasla.Infrastructure.Diagnostics;

public static class RequestDiagnosticPaths
{
    private static readonly string[] QuietPrefixes =
    [
        "/health",
        "/css",
        "/js",
        "/lib",
        "/images",
        "/img",
        "/favicon"
    ];

    private static readonly string[] HighFrequencyPaths =
    [
        "/orders/table",
        "/orders/live-data",
        "/api/print-bridge/jobs/pending"
    ];

    private static readonly string[] QuietExtensions =
    [
        ".css",
        ".js",
        ".map",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".svg",
        ".ico",
        ".woff",
        ".woff2"
    ];

    public static bool IsQuiet(PathString path)
    {
        var value = Normalize(path);
        if (value.Length == 0)
            return false;

        foreach (var prefix in QuietPrefixes)
        {
            if (value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var extension in QuietExtensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsHighFrequency(PathString path)
    {
        var value = Normalize(path);
        foreach (var candidate in HighFrequencyPaths)
        {
            if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Successful health probes and static assets stay at Debug.
    /// Failed probes and failed high-frequency calls stay visible.
    /// </summary>
    public static bool LogCompletionAtDebug(PathString path, int statusCode) =>
        statusCode < 500 && (IsQuiet(path) || (statusCode < 400 && IsHighFrequency(path)));

    private static string Normalize(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.Length > 1 && value.EndsWith('/'))
            value = value.TrimEnd('/');
        return value;
    }
}
