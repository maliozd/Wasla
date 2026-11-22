using System.CommandLine;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Cli;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Contact;

namespace Wasla.UnitTests.Cli;

/// <summary>
/// The <c>pilot-applications-retention</c> command (WAS-222) through the registered command tree, against a SQLite
/// CentralDb and a fixed clock: the dry run lists ids, statuses and UTC dates and changes nothing, a real run deletes,
/// a failed deletion exits 1 and names the row, an invalid option exits 2 before any database access, and nothing an
/// applicant typed reaches the output.
/// </summary>
[Collection(nameof(CliConsoleCollection))]
public sealed class CliPilotRetentionTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private Guid _expired;
    private Guid _recent;
    private Guid _contact;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(Ct);
        await using var db = Context();
        await db.Database.EnsureCreatedAsync(Ct);
        _expired = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-100), PilotApplicationStatus.Declined, ct: Ct);
        _recent = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-10), ct: Ct);
        _contact = await PilotRetentionTestData.AddContactMessageAsync(db, Now.AddDays(-500), Ct);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task TheDryRun_ListsTheExpiredApplication_AndChangesNothing()
    {
        using var host = Host();

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["pilot-applications-retention", "--dry-run"]));

        Assert.Equal(0, code);
        Assert.Contains("Dry run: nothing is deleted.", output, StringComparison.Ordinal);
        Assert.Contains($"would delete {_expired} status=Declined last-activity=2026-11-21T12:00:00Z due=2027-02-19T12:00:00Z", output, StringComparison.Ordinal);
        Assert.DoesNotContain(_recent.ToString(), output, StringComparison.Ordinal);
        Assert.DoesNotContain(_contact.ToString(), output, StringComparison.Ordinal);
        Assert.Contains("Expired: 1.", output, StringComparison.Ordinal);
        AssertNoPersonalData(output);
        await using var db = Context();
        Assert.Equal(3, await db.ContactMessages.CountAsync(Ct));
        Assert.False(await db.DataDestructionRecords.AnyAsync(Ct));
    }

    [Fact]
    public async Task ARealRun_DeletesTheExpiredApplicationOnly_AndARepeatDeletesNothing()
    {
        using var host = Host();

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["pilot-applications-retention", "--batch-size", "1"]));
        var (repeatCode, repeat) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["pilot-applications-retention"]));

        Assert.Equal(0, code);
        Assert.Contains($"deleted {_expired}", output, StringComparison.Ordinal);
        Assert.Contains("Deleted: 1. Kept: 0. Failed: 0.", output, StringComparison.Ordinal);
        AssertNoPersonalData(output);
        Assert.Equal(0, repeatCode);
        Assert.Contains("Expired: 0. Deleted: 0.", repeat, StringComparison.Ordinal);
        await using var db = Context();
        Assert.Equal(new[] { _recent, _contact }.Order(), (await db.ContactMessages.Select(m => m.Id).ToListAsync(Ct)).Order());
        Assert.Equal(_expired, await db.DataDestructionRecords.Select(r => r.SubjectId).SingleAsync(Ct));
    }

    [Fact]
    public async Task AFailedDeletion_ExitsWithOne_AndNamesTheRowOnly()
    {
        using var host = Host(new FailDeleteOf(_expired));

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["pilot-applications-retention"]));

        Assert.Equal(1, code);
        Assert.Contains($"FAILED {_expired} (still stored; the next run tries again)", output, StringComparison.Ordinal);
        AssertNoPersonalData(output);
        await using var db = Context();
        Assert.True(await db.ContactMessages.AnyAsync(m => m.Id == _expired, Ct));
    }

    [Fact]
    public async Task ARunThatCannotReadTheDatabase_ExitsWithOne()
    {
        await using var empty = new SqliteConnection("Data Source=:memory:");
        await empty.OpenAsync(Ct);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<CentralDbContext>(options => options.UseSqlite(empty));
                services.AddWaslaPilotApplicationRetention();
            })
            .Build();

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["pilot-applications-retention", "--dry-run"]));

        Assert.Equal(1, code);
        Assert.Contains("STOPPED EARLY: SqliteException.", output, StringComparison.Ordinal);
        Assert.Contains("Expired: 0.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatStopsPartway_PrintsWhatItDeleted_ThenTheStop_AndExitsWithOne()
    {
        using var host = Host(new FailReads(Infrastructure.Services.PilotApplicationRetentionCleanup.ExpiredBatchTag, read => read == 2 ? new InvalidOperationException("Simulated lost connection.") : null));

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["pilot-applications-retention", "--batch-size", "1"]));

        Assert.Equal(1, code);
        Assert.Contains($"deleted {_expired}", output, StringComparison.Ordinal);
        Assert.Contains("STOPPED EARLY: InvalidOperationException. The applications listed above were handled", output, StringComparison.Ordinal);
        Assert.Contains("Deleted: 1.", output, StringComparison.Ordinal);
        Assert.True(output.IndexOf($"deleted {_expired}", StringComparison.Ordinal) < output.IndexOf("STOPPED EARLY", StringComparison.Ordinal));
        AssertNoPersonalData(output);
        await using var db = Context();
        Assert.Equal(_expired, await db.DataDestructionRecords.Select(r => r.SubjectId).SingleAsync(Ct));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    public async Task ABatchSizeOutOfRange_ExitsWithTwo_BeforeAnyDatabaseAccess(string batchSize)
    {
        // A host without services: any database access would throw instead of returning the refusal.
        using var host = new HostBuilder().Build();

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["pilot-applications-retention", "--batch-size", batchSize]));

        Assert.Equal(2, code);
        Assert.Contains("--batch-size must be from 1 to 1000.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommand_IsKnownAndNeedsNoMasterKey()
    {
        Assert.True(CliHelpPrinter.IsKnownCommand("pilot-applications-retention"));
        var program = File.ReadAllText(Admin.TenantOperationsRulesTests.RepoFile("src", "Wasla.Cli", "Program.cs"));
        var exempt = program[..program.IndexOf("AesSecretManager.ValidateMasterKeyOrThrow()", StringComparison.Ordinal)];
        Assert.Contains("\"pilot-applications-retention\"", exempt, StringComparison.Ordinal);
    }

    private CentralDbContext Context() => new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    private IHost Host(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddDbContext<CentralDbContext>(options => options.UseSqlite(_connection).AddInterceptors(interceptors));
            services.AddSingleton<TimeProvider>(new ManualClock(new DateTimeOffset(Now)));
            services.AddWaslaPilotApplicationRetention();
        })
        .Build();

    private static void AssertNoPersonalData(string output) =>
        Assert.All(PilotRetentionTestData.PersonalValues, value => Assert.DoesNotContain(value, output, StringComparison.OrdinalIgnoreCase));

    private static async Task<(int Code, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var code = await action();
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
