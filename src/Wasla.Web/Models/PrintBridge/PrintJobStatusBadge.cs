using Wasla.Domain.Enums;

namespace Wasla.Web.Models.PrintBridge;

public static class PrintJobStatusBadge
{
    public static (string BadgeClass, string RowClass) Get(PrintJobStatus status) =>
        status switch
        {
            PrintJobStatus.Pending => ("text-bg-secondary", "wasla-print-job-row--pending"),
            PrintJobStatus.Printing => ("text-bg-info", "wasla-print-job-row--printing"),
            PrintJobStatus.Printed => ("text-bg-success", "wasla-print-job-row--printed"),
            PrintJobStatus.Failed => ("text-bg-danger", "wasla-print-job-row--failed"),
            PrintJobStatus.Cancelled => ("text-bg-secondary", "wasla-print-job-row--cancelled"),
            _ => ("text-bg-secondary", "")
        };

    public static (string BadgeClass, string RowClass) Get(string status) =>
        Enum.TryParse<PrintJobStatus>(status, out var parsed)
            ? Get(parsed)
            : ("text-bg-secondary", "");
}
