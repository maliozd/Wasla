namespace Wasla.PrintBridge.Models;

public enum PrintBridgeRuntimeIssueCode
{
    ServerUnreachable,
    SslError,
    EndpointNotFound,
    RequestFailed,
    ReconnectRequired,
    DisabledByAdmin,
    DuplicateInstallation,
    Localized,
    Unexpected
}

public sealed record PrintBridgeRuntimeIssue(
    PrintBridgeRuntimeIssueCode Code,
    string? ResourceKey = null,
    object[]? Args = null,
    string? RawMessage = null)
{
    public bool ShouldClearToken =>
        Code == PrintBridgeRuntimeIssueCode.ReconnectRequired;

    public bool ShouldStopPolling =>
        Code is PrintBridgeRuntimeIssueCode.ReconnectRequired
            or PrintBridgeRuntimeIssueCode.DisabledByAdmin
            or PrintBridgeRuntimeIssueCode.DuplicateInstallation;

    public string EffectiveResourceKey =>
        ResourceKey ?? Code switch
        {
            PrintBridgeRuntimeIssueCode.ServerUnreachable => "Connection.ServerUnreachable",
            PrintBridgeRuntimeIssueCode.SslError => "Connection.SslError",
            PrintBridgeRuntimeIssueCode.EndpointNotFound => "Connection.EndpointNotFound",
            PrintBridgeRuntimeIssueCode.RequestFailed => "Connection.RequestFailed",
            PrintBridgeRuntimeIssueCode.ReconnectRequired => "RuntimeIssue.ReconnectRequired",
            PrintBridgeRuntimeIssueCode.DisabledByAdmin => "RuntimeIssue.DisabledByAdmin",
            PrintBridgeRuntimeIssueCode.DuplicateInstallation => "RuntimeIssue.DuplicateInstallation",
            _ => string.Empty
        };

    public static PrintBridgeRuntimeIssue FromResource(string resourceKey, params object[] args) =>
        new(PrintBridgeRuntimeIssueCode.Localized, resourceKey, args);

    public static PrintBridgeRuntimeIssue FromRaw(string message) =>
        new(PrintBridgeRuntimeIssueCode.Unexpected, RawMessage: message);
}
