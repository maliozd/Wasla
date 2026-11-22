using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Contact;
using Microsoft.Data.SqlClient;

namespace Wasla.Cli;

/// <summary>
/// <c>pilot-applications-retention</c> (WAS-222): deletes pilot applications whose retention period has ended, or with
/// <c>--dry-run</c> lists them and changes nothing. Prints ids, statuses and UTC times only, never what an applicant
/// typed. Exit code 0 when every expired application was handled, 1 when one or more could not be deleted (they stay
/// stored for the next run) or the run failed, 2 for invalid options; a scheduler alerts on a non-zero exit.
/// </summary>
internal static class CliPilotRetentionCommands
{
    public static async Task<int> RunAsync(IHost host, bool dryRun, int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > PilotRetentionRun.MaxBatchSize)
        {
            Console.Error.WriteLine($"--batch-size must be from 1 to {PilotRetentionRun.MaxBatchSize}.");
            return 2;
        }

        PilotRetentionRunResult result;
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IPilotApplicationRetentionService>();
            result = await service.RunAsync(new PilotRetentionRunRequest(dryRun, batchSize), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"The pilot application retention run failed before it finished; nothing more was deleted. {ex.GetType().Name}" +
                (SqlErrorNumber(ex) is { } number ? $" (SQL Server error {number})" : string.Empty));
            return 1;
        }

        Print(result);
        return result.Succeeded ? 0 : 1;
    }

    private static void Print(PilotRetentionRunResult result)
    {
        Console.WriteLine(
            $"Pilot application retention: {PilotApplicationRetention.PeriodDays} days after the last activity. " +
            $"Now {Utc(result.NowUtc)}. {(result.DryRun ? "Dry run: nothing is deleted." : "Deleting.")}");

        if (result.DryRun)
        {
            foreach (var candidate in result.Expired)
            {
                Console.WriteLine(
                    $"would delete {candidate.Id} status={candidate.Status} last-activity={Utc(candidate.LastActivityAtUtc)} due={Utc(candidate.DeleteOnOrAfterUtc)}");
            }
        }
        else
        {
            foreach (var id in result.Deleted)
                Console.WriteLine($"deleted {id}");
            foreach (var id in result.NoLongerExpired)
                Console.WriteLine($"kept {id} (changed since it was read: new activity, a running notification, or already deleted)");
            foreach (var id in result.Failed)
                Console.Error.WriteLine($"FAILED {id} (still stored; the next run tries again)");
        }

        Console.WriteLine(result.DryRun
            ? $"Expired: {result.Expired.Count}. Destruction records past their {Domain.Entities.Central.DataDestructionRecord.RetentionYears}-year retention: {result.DestructionRecordsPastRetention}."
            : $"Expired: {result.Expired.Count}. Deleted: {result.Deleted.Count}. Kept: {result.NoLongerExpired.Count}. Failed: {result.Failed.Count}. " +
              $"Destruction records removed after {Domain.Entities.Central.DataDestructionRecord.RetentionYears} years: {result.DestructionRecordsPastRetention}.");
    }

    private static int? SqlErrorNumber(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
                return sql.Number;
        }

        return null;
    }

    private static string Utc(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
