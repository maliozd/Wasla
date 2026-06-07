namespace OrderHub.PrintBridge.Models;

public sealed class LocalPrintJobRecord
{
    public Guid JobId { get; init; }
    public Guid OrderId { get; init; }
    public string ShortJobId => JobId.ToString("N")[..8];
    public string? OrderDisplay { get; set; }
    public LocalPrintJobStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? PrintedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StatusNote { get; set; }

    public string StatusDisplay => Status switch
    {
        LocalPrintJobStatus.Received => "Received",
        LocalPrintJobStatus.Printing => "Printing",
        LocalPrintJobStatus.Printed => string.IsNullOrWhiteSpace(StatusNote) ? "Printed" : $"Printed ({StatusNote})",
        LocalPrintJobStatus.Failed => "Failed",
        LocalPrintJobStatus.Skipped => "Skipped",
        _ => Status.ToString()
    };

    public DateTime DisplayTimeUtc => PrintedAtUtc ?? LastAttemptAtUtc ?? CreatedAtUtc;
}
