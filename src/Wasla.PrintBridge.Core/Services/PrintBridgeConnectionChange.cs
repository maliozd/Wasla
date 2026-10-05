namespace Wasla.PrintBridge.Services;

/// <summary>Result of <see cref="PrintBridgeRuntime.ApplyVerifiedConnectionAsync"/>.</summary>
public enum PrintBridgeConnectionChange
{
    /// <summary>The verified connection is saved and active, and the engine is listening.</summary>
    AppliedListening,

    /// <summary>
    /// The verified connection is saved and active. The engine is not listening: either that was not requested,
    /// or printing is not ready yet (for example, no printer is chosen).
    /// </summary>
    AppliedNotListening,

    /// <summary>The settings file could not be written. The previous connection is still in use.</summary>
    SaveFailed,

    /// <summary>Abandoned before anything was written, because the app is closing.</summary>
    Abandoned
}
