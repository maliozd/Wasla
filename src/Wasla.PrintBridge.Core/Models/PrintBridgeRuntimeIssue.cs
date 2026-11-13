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
    Unexpected,

    /// <summary>The app cannot reach the Print Bridge service (not installed, not running, or restarting). WAS-61.</summary>
    ServiceUnavailable,

    /// <summary>The app and the service speak different IPC protocol versions. WAS-61.</summary>
    ServiceIncompatible,

    /// <summary>The service waits because another Print Bridge engine holds the machine-wide engine lock. WAS-61.</summary>
    EngineHeldElsewhere,

    /// <summary>The service refuses to start its engine because its data folder is not protected. WAS-61.</summary>
    DataFolderNotSecured
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
            or PrintBridgeRuntimeIssueCode.DuplicateInstallation;

    public bool IsBlockingLifecycleIssue =>
        Code is PrintBridgeRuntimeIssueCode.ReconnectRequired
            or PrintBridgeRuntimeIssueCode.DisabledByAdmin
            or PrintBridgeRuntimeIssueCode.DuplicateInstallation
        || IsServiceIssue;

    /// <summary>
    /// The engine cannot be used at all (WAS-61): the app cannot reach the service, or the service cannot run its engine.
    /// No action is offered until it is resolved.
    /// </summary>
    public bool IsServiceIssue =>
        Code is PrintBridgeRuntimeIssueCode.ServiceUnavailable
            or PrintBridgeRuntimeIssueCode.ServiceIncompatible
            or PrintBridgeRuntimeIssueCode.EngineHeldElsewhere
            or PrintBridgeRuntimeIssueCode.DataFolderNotSecured;

    public string EffectiveTitleResourceKey =>
        Code switch
        {
            PrintBridgeRuntimeIssueCode.ReconnectRequired => "RuntimeIssue.ReconnectRequired.Title",
            PrintBridgeRuntimeIssueCode.DisabledByAdmin => "RuntimeIssue.DisabledByAdmin.Title",
            PrintBridgeRuntimeIssueCode.DuplicateInstallation => "RuntimeIssue.DuplicateInstallation.Title",
            PrintBridgeRuntimeIssueCode.ServiceUnavailable => "RuntimeIssue.ServiceUnavailable.Title",
            PrintBridgeRuntimeIssueCode.ServiceIncompatible => "RuntimeIssue.ServiceIncompatible.Title",
            PrintBridgeRuntimeIssueCode.EngineHeldElsewhere => "RuntimeIssue.EngineHeldElsewhere.Title",
            PrintBridgeRuntimeIssueCode.DataFolderNotSecured => "RuntimeIssue.DataFolderNotSecured.Title",
            _ => string.Empty
        };

    public string EffectiveConnectionSummaryResourceKey =>
        Code switch
        {
            PrintBridgeRuntimeIssueCode.DisabledByAdmin => "ConnectionSummary.Disabled",
            PrintBridgeRuntimeIssueCode.ReconnectRequired
                or PrintBridgeRuntimeIssueCode.DuplicateInstallation => "ConnectionSummary.NotConnected",
            _ when IsServiceIssue => "ConnectionSummary.ServiceUnavailable",
            _ => string.Empty
        };

    public string EffectiveDetailResourceKey =>
        ResourceKey ?? Code switch
        {
            PrintBridgeRuntimeIssueCode.ServerUnreachable => "Connection.ServerUnreachable",
            PrintBridgeRuntimeIssueCode.SslError => "Connection.SslError",
            PrintBridgeRuntimeIssueCode.EndpointNotFound => "Connection.EndpointNotFound",
            PrintBridgeRuntimeIssueCode.RequestFailed => "Connection.RequestFailed",
            PrintBridgeRuntimeIssueCode.ReconnectRequired => "RuntimeIssue.ReconnectRequired.Detail",
            PrintBridgeRuntimeIssueCode.DisabledByAdmin => "RuntimeIssue.DisabledByAdmin.Detail",
            PrintBridgeRuntimeIssueCode.DuplicateInstallation => "RuntimeIssue.DuplicateInstallation.Detail",
            PrintBridgeRuntimeIssueCode.ServiceUnavailable => "RuntimeIssue.ServiceUnavailable.Detail",
            PrintBridgeRuntimeIssueCode.ServiceIncompatible => "RuntimeIssue.ServiceIncompatible.Detail",
            PrintBridgeRuntimeIssueCode.EngineHeldElsewhere => "RuntimeIssue.EngineHeldElsewhere.Detail",
            PrintBridgeRuntimeIssueCode.DataFolderNotSecured => "RuntimeIssue.DataFolderNotSecured.Detail",
            _ => string.Empty
        };

    public string EffectiveResourceKey => EffectiveDetailResourceKey;

    public static PrintBridgeRuntimeIssue FromResource(string resourceKey, params object[] args) =>
        new(PrintBridgeRuntimeIssueCode.Localized, resourceKey, args);

    public static PrintBridgeRuntimeIssue FromRaw(string message) =>
        new(PrintBridgeRuntimeIssueCode.Unexpected, RawMessage: message);
}
