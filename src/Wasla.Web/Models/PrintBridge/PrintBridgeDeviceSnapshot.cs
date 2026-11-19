using System.Text.Json;
using Wasla.Application.Abstractions.Printing;

namespace Wasla.Web.Models.PrintBridge;

/// <summary>
/// The Print Bridge Devices page's device snapshot. The page's initial state, <c>GET /print-bridge/devices/list</c> and the
/// device toggle's response all carry this one contract, so the page has a single rendering path.
/// <para>
/// Every timestamp is UTC and serializes with <c>Z</c>. <see cref="ServerTimeUtc"/> lets the browser correct its own
/// clock, and the thresholds are <see cref="PrintBridgeConnectionStatusCalculator"/>'s own, so the page can stop showing
/// a device as connected once its heartbeat is too old without inventing a rule of its own.
/// </para>
/// </summary>
public sealed record PrintBridgeDeviceSnapshot(
    IReadOnlyList<PrintBridgeDeviceSnapshotRow> Devices,
    PrintBridgeDeviceSnapshotSummary Summary,
    DateTime ServerTimeUtc,
    int ConnectedThresholdSeconds,
    int RecentlySeenThresholdSeconds)
{
    /// <summary>The same camelCase shape MVC uses for the JSON endpoints, for the page's inline initial state.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <param name="devices">The tenant-scoped device list; the caller never mixes tenants. The summary counts come from it.</param>
    /// <param name="utcNow">The server's current UTC time.</param>
    /// <param name="detailsUrl">The device details link for an id.</param>
    public static PrintBridgeDeviceSnapshot Create(
        IReadOnlyList<PrintBridgeDeviceSummaryDto> devices,
        DateTime utcNow,
        Func<Guid, string> detailsUrl)
    {
        var rows = devices
            .Select(d => new PrintBridgeDeviceSnapshotRow(
                d.Id,
                d.Name,
                d.IsActive,
                d.ConnectionStatus.ToString(),
                d.ConnectionStatusLabelKey,
                d.IsConnected,
                AsUtc(d.LastSeenAtUtc),
                d.MachineName,
                d.LocalAlias,
                d.PrinterName,
                d.AppVersion,
                detailsUrl(d.Id)))
            .ToList();

        var lastConnected = rows
            .Where(d => d.LastSeenAtUtc.HasValue)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .FirstOrDefault();

        return new PrintBridgeDeviceSnapshot(
            rows,
            new PrintBridgeDeviceSnapshotSummary(
                rows.Count(d => d.IsActive),
                rows.Count(d => d.ConnectionStatus == nameof(PrintBridgeConnectionStatus.Connected)),
                lastConnected?.LastSeenAtUtc,
                lastConnected?.Name),
            AsUtc(utcNow)!.Value,
            (int)PrintBridgeConnectionStatusCalculator.ConnectedThreshold.TotalSeconds,
            (int)PrintBridgeConnectionStatusCalculator.RecentlySeenThreshold.TotalSeconds);
    }

    /// <summary>
    /// Stored device times are UTC but come back from the database without a kind; mark them as UTC (a local value is
    /// converted), so they serialize with <c>Z</c> instead of as an ambiguous wall-clock time.
    /// </summary>
    public static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        { } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc)
    };
}

/// <summary>One manageable device of the current tenant, as the Devices page shows it.</summary>
public sealed record PrintBridgeDeviceSnapshotRow(
    Guid Id,
    string Name,
    bool IsActive,
    string ConnectionStatus,
    string ConnectionStatusLabelKey,
    bool IsConnected,
    DateTime? LastSeenAtUtc,
    string? MachineName,
    string? LocalAlias,
    string? PrinterName,
    string? AppVersion,
    string DetailsUrl);

/// <summary>
/// The operational counts of the same device list: active devices and devices online now. They describe the tenant's
/// devices; no count is a limit (there is no device limit, decision of 2026-11-17).
/// </summary>
public sealed record PrintBridgeDeviceSnapshotSummary(
    int ActiveDeviceCount,
    int ConnectedDeviceCount,
    DateTime? LatestLastSeenAtUtc,
    string? LastConnectedDeviceName);
