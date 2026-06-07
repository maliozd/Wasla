using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderHub.PrintBridge.Options;
using OrderHub.PrintBridge.Printing;

namespace OrderHub.PrintBridge.Services;

public sealed class PrintBridgeWorker : BackgroundService
{
    private readonly OrderHubPrintBridgeClient _client;
    private readonly ReceiptFormatter _formatter;
    private readonly IReceiptPrinter _printer;
    private readonly PrintBridgeOptions _options;
    private readonly ILogger<PrintBridgeWorker> _logger;

    public PrintBridgeWorker(
        OrderHubPrintBridgeClient client,
        ReceiptFormatter formatter,
        IReceiptPrinter printer,
        PrintBridgeOptions options,
        ILogger<PrintBridgeWorker> logger)
    {
        _client = client;
        _formatter = formatter;
        _printer = printer;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OrderHub Print Bridge started. PrinterMode={PrinterMode}, PrinterName={PrinterName}, DryRun={DryRun}, BridgeName={BridgeName}, MaxJobsPerPoll={MaxJobsPerPoll}",
            _options.PrinterMode,
            string.IsNullOrWhiteSpace(_options.PrinterName) ? "(not set)" : _options.PrinterName,
            _options.DryRun,
            _options.BridgeName,
            _options.MaxJobsPerPoll);

        while (!stoppingToken.IsCancellationRequested)
        {
            var waitSeconds = _options.IdlePollIntervalSeconds;
            var hadJobs = false;
            var hadError = false;

            try
            {
                var jobs = await _client.GetPendingJobsAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Polling result: pending job count={Count}", jobs.Count);

                if (jobs.Count > 0)
                {
                    hadJobs = true;
                    foreach (var job in jobs)
                    {
                        await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                hadError = true;
                _logger.LogWarning(ex, "Print Bridge polling/processing failed. API or server may be unavailable.");
            }

            waitSeconds = hadError
                ? Math.Max(1, _options.ErrorPollIntervalSeconds)
                : hadJobs
                    ? Math.Max(1, _options.BusyPollIntervalSeconds)
                    : Math.Max(1, _options.IdlePollIntervalSeconds);

            _logger.LogDebug(
                "Next poll in {WaitSeconds}s ({Mode})",
                waitSeconds,
                hadError ? "error" : hadJobs ? "busy" : "idle");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("OrderHub Print Bridge stopping.");
    }

    private async Task ProcessJobAsync(OrderHubPrintBridgeClient.PendingPrintJobDto job, CancellationToken ct)
    {
        _logger.LogInformation("Job claim attempted. JobId={JobId}, OrderId={OrderId}", job.Id, job.OrderId);

        OrderHubPrintBridgeClient.PrintJobActionResult claim;
        try
        {
            claim = await _client.MarkPrintingAsync(job.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job claim request failed. JobId={JobId}", job.Id);
            throw;
        }

        if (claim.Skipped)
        {
            _logger.LogInformation("Job claim skipped because job is no longer Pending. JobId={JobId}", job.Id);
            return;
        }

        if (!claim.Success)
        {
            _logger.LogWarning("Job claim failed. JobId={JobId}, Result={Result}", job.Id, claim.Result);
            return;
        }

        _logger.LogInformation("Job claim succeeded. JobId={JobId}", job.Id);

        try
        {
            var receipt = _formatter.Format(job.PayloadJson);

            if (_options.DryRun)
            {
                _logger.LogInformation("DryRun receipt output for JobId={JobId}:\n{Receipt}", job.Id, receipt);
                _logger.LogInformation("DryRun: no physical print was sent. JobId={JobId}", job.Id);
            }
            else
            {
                _logger.LogInformation(
                    "Print send attempted. JobId={JobId}, PrinterName={PrinterName}, CopyCount={CopyCount}",
                    job.Id,
                    _options.PrinterName,
                    job.CopyCount);
                await _printer.PrintAsync(_options.PrinterName, receipt, job.CopyCount, ct).ConfigureAwait(false);
                _logger.LogInformation("Print succeeded. JobId={JobId}, CopyCount={CopyCount}", job.Id, job.CopyCount);
            }

            var printed = await _client.MarkPrintedAsync(job.Id, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "mark-printed result. JobId={JobId}, Success={Success}, Skipped={Skipped}, Result={Result}",
                job.Id,
                printed.Success,
                printed.Skipped,
                printed.Result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print failed. JobId={JobId}", job.Id);
            try
            {
                var failed = await _client.MarkFailedAsync(job.Id, ex.Message, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "mark-failed result. JobId={JobId}, Success={Success}, Skipped={Skipped}, Result={Result}",
                    job.Id,
                    failed.Success,
                    failed.Skipped,
                    failed.Result);
            }
            catch (Exception markEx)
            {
                _logger.LogWarning(markEx, "Failed to mark job as failed. JobId={JobId}", job.Id);
                throw;
            }
        }
    }

}
