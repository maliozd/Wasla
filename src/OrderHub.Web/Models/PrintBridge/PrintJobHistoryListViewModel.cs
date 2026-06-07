namespace OrderHub.Web.Models.PrintBridge;

public sealed class PrintJobHistoryListViewModel
{
    public IReadOnlyList<PrintJobHistoryRowViewModel> Jobs { get; set; } =
        Array.Empty<PrintJobHistoryRowViewModel>();
}
