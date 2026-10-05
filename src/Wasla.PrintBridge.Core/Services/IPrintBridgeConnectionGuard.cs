namespace Wasla.PrintBridge.Services;

/// <summary>
/// The one decision about whether the saved server URL and device token may change now. Every path that replaces them
/// (a setup link, the app's connection dialog, the classic window's save and test) asks here first.
/// </summary>
public interface IPrintBridgeConnectionGuard
{
    /// <summary>
    /// Returns null while a print job is active: from the moment it is about to be claimed until its last report,
    /// including <c>mark-printed</c> retries and a job that still finishes after Stop returned. Otherwise returns a
    /// lease: until it is disposed, no new job is claimed, so the connection can change without splitting a job.
    /// </summary>
    IDisposable? TryBeginConnectionChange();
}
