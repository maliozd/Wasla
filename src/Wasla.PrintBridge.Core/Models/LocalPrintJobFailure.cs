namespace Wasla.PrintBridge.Models;

/// <summary>What the app may say about a failed job: never its error text, which can be technical or echo a server.</summary>
public enum LocalPrintJobFailureCategory
{
    Printer,
    Server,
    Other
}

/// <summary>Classifies the engine's own error text of a failed job (moved from the app's history view in WAS-61).</summary>
public static class LocalPrintJobFailure
{
    private static readonly string[] PrinterErrorPrefixes =
    [
        "Printer not found",
        "Unable to open printer",
        "Unable to start print",
        "Unable to write to printer",
        "PrinterName is required"
    ];

    private static readonly string[] ServerErrorPrefixes = ["Claim failed:", "mark-printed:"];

    public static LocalPrintJobFailureCategory Classify(string? errorMessage)
    {
        var error = errorMessage ?? string.Empty;
        if (PrinterErrorPrefixes.Any(p => error.StartsWith(p, StringComparison.Ordinal)))
            return LocalPrintJobFailureCategory.Printer;
        if (ServerErrorPrefixes.Any(p => error.StartsWith(p, StringComparison.Ordinal)))
            return LocalPrintJobFailureCategory.Server;
        return LocalPrintJobFailureCategory.Other;
    }

    /// <summary>A copy a client may receive: the error text replaced by its category.</summary>
    public static LocalPrintJobRecord ForClient(LocalPrintJobRecord source) =>
        new()
        {
            JobId = source.JobId,
            OrderId = source.OrderId,
            OrderDisplay = source.OrderDisplay,
            Platform = source.Platform,
            JobType = source.JobType,
            PrinterName = source.PrinterName,
            Status = source.Status,
            CreatedAtUtc = source.CreatedAtUtc,
            LastAttemptAtUtc = source.LastAttemptAtUtc,
            PrintedAtUtc = source.PrintedAtUtc,
            ErrorMessage = null,
            StatusNote = source.StatusNote,
            FailureCategory = source.Status == LocalPrintJobStatus.Failed
                ? source.FailureCategory ?? Classify(source.ErrorMessage)
                : null
        };
}
