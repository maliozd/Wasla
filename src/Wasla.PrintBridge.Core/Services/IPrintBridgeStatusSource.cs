using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.Services;

/// <summary>
/// Read-only view of the running Print Bridge engine. Presentation shells observe the engine through
/// this interface; it deliberately exposes no way to start, stop, poll, claim or print.
/// </summary>
public interface IPrintBridgeStatusSource
{
    /// <summary>Raised from any thread when the engine state may have changed.</summary>
    event EventHandler? StatusChanged;

    PrintBridgeRuntimeStatus GetStatus();
}
