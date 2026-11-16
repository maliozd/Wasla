namespace Wasla.PrintBridge.Models;

/// <summary>
/// The selected printer as Windows reports it. <see cref="Ready"/> means the queue exists and Windows reports no problem
/// (WAS-216): it is not proof that the printer works or that paper comes out.
/// </summary>
public enum PrinterHealthStatus
{
    Ready,
    NotConfigured,
    NotFound,
    DryRun,

    /// <summary>WAS-216: Windows reports the printer offline or not available; new jobs are not claimed.</summary>
    Offline,

    /// <summary>WAS-216: printing is paused in Windows; new jobs are not claimed.</summary>
    Paused,

    /// <summary>WAS-216: Windows reports an error (paper, door, jam, intervention) or the queue could not be read.</summary>
    Error
}
