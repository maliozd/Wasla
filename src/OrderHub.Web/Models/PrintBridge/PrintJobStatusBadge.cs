using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.PrintBridge;

public static class PrintJobStatusBadge
{
    public static (string BadgeClass, string RowClass) Get(PrintJobStatus status) =>
        status switch
        {
            PrintJobStatus.Pending => ("text-bg-secondary", "oh-print-job-row--pending"),
            PrintJobStatus.Printing => ("text-bg-info", "oh-print-job-row--printing"),
            PrintJobStatus.Printed => ("text-bg-success", "oh-print-job-row--printed"),
            PrintJobStatus.Failed => ("text-bg-danger", "oh-print-job-row--failed"),
            PrintJobStatus.Cancelled => ("text-bg-secondary", "oh-print-job-row--cancelled"),
            _ => ("text-bg-secondary", "")
        };

    public static (string BadgeClass, string RowClass) Get(string status) =>
        Enum.TryParse<PrintJobStatus>(status, out var parsed)
            ? Get(parsed)
            : ("text-bg-secondary", "");
}
