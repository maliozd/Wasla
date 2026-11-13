namespace Wasla.PrintBridge.Models;

public enum LocalPrintJobStatus
{
    Received,
    Printing,
    Printed,
    Failed,
    Skipped,

    /// <summary>
    /// WAS-61: interrupted while it was being sent to the printer; whether it printed is unknown. Shown as needing a check
    /// until the user says whether it printed.
    /// </summary>
    Uncertain
}
