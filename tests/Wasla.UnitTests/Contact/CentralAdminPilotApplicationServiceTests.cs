using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The Central Admin side of a pilot application (WAS-222) over a real SQLite CentralDb: one person's application found
/// by the exact email address or phone number, its data exported, corrected and deleted on request with a destruction
/// record naming the administrator, and the team's status and recorded contacts moving the last activity. Contact form
/// messages are never found, changed or deleted. Fictional data only.
/// </summary>
public sealed class CentralAdminPilotApplicationServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AdminId = Guid.Parse("7d4b3c1e-2a0f-4f55-9a61-0d2b6c8e5a11");

    private readonly CentralTestDatabase _central = new();

    public CentralAdminPilotApplicationServiceTests()
    {
        using var db = _central.CreateContext();
        db.Cities.AddRange(PilotProvinceSeed.All());
        db.SaveChanges();
    }

    public void Dispose() => _central.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Find_MatchesTheExactEmailIgnoringCase_OrTheExactPhone_AndOnlyPilotApplications()
    {
        Guid first, second, other;
        await using (var db = _central.CreateContext())
        {
            first = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-20), receivedAtUtc: Now.AddDays(-20), ct: Ct);
            second = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-2), receivedAtUtc: Now.AddDays(-2), ct: Ct);
            other = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-1), ct: Ct);
            await db.ContactMessages.Where(m => m.Id == other).ExecuteUpdateAsync(s => s.SetProperty(m => m.Phone, "5559998877"), Ct);
            await PilotRetentionTestData.AddContactMessageAsync(db, Now.AddDays(-3), Ct);
        }

        await using var context = _central.CreateContext();
        var service = Service(context);

        var byEmail = await service.FindAsync("  " + PilotRetentionTestData.Email(first).ToUpperInvariant() + " ", null, Ct);
        Assert.Equal(first, Assert.Single(byEmail).Id);

        // The phone number the fictional applications share, newest first; the one with another number is not listed.
        var byPhone = await service.FindAsync(null, PilotRetentionTestData.Phone, Ct);
        Assert.Equal(new[] { second, first }, byPhone.Select(r => r.Id));
        Assert.All(byPhone, r => Assert.Equal(PilotApplicationStatus.New, r.Status));

        Assert.Empty(await service.FindAsync("deniz", null, Ct));
        Assert.Empty(await service.FindAsync(null, PilotRetentionTestData.Phone[..6], Ct));
        Assert.Empty(await service.FindAsync("old.contact@retention.test", null, Ct));
        Assert.Empty(await service.FindAsync(" ", null, Ct));
    }

    [Fact]
    public async Task Find_ListsAtMostTwentyApplications()
    {
        await using (var db = _central.CreateContext())
        {
            for (var i = 0; i < 25; i++)
                await PilotRetentionTestData.AddPilotAsync(db, Now.AddMinutes(-i), ct: Ct);
        }

        await using var context = _central.CreateContext();
        Assert.Equal(ICentralAdminPilotApplicationService.MaxSearchResults, (await Service(context).FindAsync(null, PilotRetentionTestData.Phone, Ct)).Count);
    }

    [Fact]
    public async Task Export_HoldsEverythingStoredAboutTheApplication_AndNothingForAContactMessage()
    {
        Guid id, contact;
        await using (var db = _central.CreateContext())
        {
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-5), PilotApplicationStatus.Contacted, Now.AddDays(-30), ct: Ct);
            contact = await PilotRetentionTestData.AddContactMessageAsync(db, Now, Ct);
        }

        await using var context = _central.CreateContext();
        var export = await Service(context).ExportAsync(id, Ct);

        Assert.NotNull(export);
        Assert.Equal((id, Now.AddDays(-30)), (export.Id, export.ReceivedAtUtc));
        Assert.Equal((PilotRetentionTestData.RestaurantName, PilotRetentionTestData.ContactName), (export.RestaurantName, export.ContactName));
        Assert.Equal((PilotRetentionTestData.Email(id), PilotRetentionTestData.Phone), (export.Email, export.Phone));
        Assert.Equal(("34", "İstanbul"), (export.CityPlateCode, export.CityName));
        Assert.Equal((PilotRetentionTestData.Note, "tr-TR"), (export.Note, export.FormLanguage));
        Assert.Equal((PilotApplicationStatus.Contacted, Now.AddDays(-5), Now.AddDays(85)), (export.Status, export.LastActivityAtUtc, export.DeleteOnOrAfterUtc));
        Assert.Equal(ContactMessageNotificationStatus.Sent, export.TeamNotificationStatus);
        Assert.Null(await Service(context).ExportAsync(contact, Ct));
        Assert.Null(await Service(context).ExportAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Correct_ChangesThePersonalData_WithTheFormsRules_AndKeepsTheRetentionPeriod()
    {
        Guid id, contact;
        await using (var db = _central.CreateContext())
        {
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-40), PilotApplicationStatus.Contacted, ct: Ct);
            contact = await PilotRetentionTestData.AddContactMessageAsync(db, Now, Ct);
        }

        var correction = new PilotApplicationCorrection(" Yeni Ad Lokantası ", "Deniz Yeni", "deniz.yeni@retention.test", "2163334455", "06", " Yeni not ");
        await using (var context = _central.CreateContext())
        {
            var service = Service(context);
            Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.CorrectAsync(id, correction, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.Invalid, await service.CorrectAsync(id, correction with { CityPlateCode = "99" }, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.Invalid, await service.CorrectAsync(id, correction with { PhoneDigits = "12345" }, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.Invalid, await service.CorrectAsync(id, correction with { Email = "not-an-address" }, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.NotFound, await service.CorrectAsync(contact, correction, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.NotFound, await service.CorrectAsync(Guid.NewGuid(), correction, Ct));
        }

        await using var check = _central.CreateContext();
        var row = await check.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == id, Ct);
        Assert.Equal(("Yeni Ad Lokantası", "Deniz Yeni", "deniz.yeni@retention.test"), (row.RestaurantName, row.FullName, row.Email));
        Assert.Equal(("2163334455", "06", "Yeni not"), (row.Phone, row.CityPlateCode, row.Message));
        Assert.Equal(Now.AddDays(-40), row.PilotLastActivityAtUtc);
        Assert.Equal("Old Contact Sender", (await check.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == contact, Ct)).FullName);
    }

    [Fact]
    public async Task SetStatusAndRecordContact_MoveTheLastActivityToNow_AndLeaveContactMessagesAlone()
    {
        Guid id, contact;
        await using (var db = _central.CreateContext())
        {
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-60), ct: Ct);
            contact = await PilotRetentionTestData.AddContactMessageAsync(db, Now.AddDays(-60), Ct);
        }

        var clock = new ManualClock(new DateTimeOffset(Now));
        await using var context = _central.CreateContext();
        var service = Service(context, clock);

        Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.SetStatusAsync(id, PilotApplicationStatus.Contacted, Ct));
        Assert.Equal((PilotApplicationStatus.Contacted, Now), await StateAsync(id));

        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(PilotApplicationChangeOutcome.Unchanged, await service.SetStatusAsync(id, PilotApplicationStatus.Contacted, Ct));
        Assert.Equal((PilotApplicationStatus.Contacted, Now), await StateAsync(id));
        Assert.Equal(PilotApplicationChangeOutcome.Invalid, await service.SetStatusAsync(id, PilotApplicationStatus.New, Ct));
        Assert.Equal(PilotApplicationChangeOutcome.Invalid, await service.SetStatusAsync(id, (PilotApplicationStatus)42, Ct));

        Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.RecordContactAsync(id, Ct));
        Assert.Equal((PilotApplicationStatus.Contacted, Now.AddDays(3)), await StateAsync(id));

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.SetStatusAsync(id, PilotApplicationStatus.Declined, Ct));
        Assert.Equal((PilotApplicationStatus.Declined, Now.AddDays(4)), await StateAsync(id));

        Assert.Equal(PilotApplicationChangeOutcome.NotFound, await service.SetStatusAsync(contact, PilotApplicationStatus.Accepted, Ct));
        Assert.Equal(PilotApplicationChangeOutcome.NotFound, await service.RecordContactAsync(contact, Ct));
        await using var check = _central.CreateContext();
        var untouched = await check.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == contact, Ct);
        Assert.Equal(((PilotApplicationStatus?)null, (DateTime?)null), (untouched.PilotStatus, untouched.PilotLastActivityAtUtc));
    }

    [Fact]
    public async Task DeleteOnRequest_DeletesTheApplication_WithARecordNamingTheAdministrator_Once()
    {
        Guid id, contact;
        await using (var db = _central.CreateContext())
        {
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-1), PilotApplicationStatus.Contacted, ct: Ct);
            contact = await PilotRetentionTestData.AddContactMessageAsync(db, Now.AddDays(-1), Ct);
        }

        var logger = new CapturingLogger<CentralAdminPilotApplicationService>();
        await using (var context = _central.CreateContext())
        {
            var service = Service(context, logger: logger);
            Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.DeleteOnRequestAsync(id, AdminId, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.NotFound, await service.DeleteOnRequestAsync(id, AdminId, Ct));
            Assert.Equal(PilotApplicationChangeOutcome.NotFound, await service.DeleteOnRequestAsync(contact, AdminId, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteOnRequestAsync(id, Guid.Empty, Ct));
        }

        await using var check = _central.CreateContext();
        Assert.False(await check.ContactMessages.AnyAsync(m => m.Id == id, Ct));
        Assert.True(await check.ContactMessages.AnyAsync(m => m.Id == contact, Ct));
        var record = await check.DataDestructionRecords.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((id, DataDestructionMethod.DataSubjectRequest, DataDestructionResult.Deleted), (record.SubjectId, record.Method, record.Result));
        Assert.Equal((DataDestructionActor.CentralAdmin, (Guid?)AdminId, Now), (record.Actor, record.ActorUserId, record.OccurredAtUtc));
        Assert.All(logger.Entries, e => Assert.All(PilotRetentionTestData.PersonalValues, v => Assert.DoesNotContain(v, e.Text, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Text.Contains(id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteOnRequest_WaitsForARunningNotificationAttempt()
    {
        Guid id;
        await using (var db = _central.CreateContext())
        {
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-1), ct: Ct);
            await PilotRetentionTestData.ClaimNotificationAsync(db, id, Now.AddMinutes(-1), Ct);
        }

        var clock = new ManualClock(new DateTimeOffset(Now));
        await using var context = _central.CreateContext();
        var service = Service(context, clock);

        Assert.Equal(PilotApplicationChangeOutcome.InProgress, await service.DeleteOnRequestAsync(id, AdminId, Ct));
        Assert.False(await context.DataDestructionRecords.AnyAsync(Ct));

        clock.Advance(ContactNotificationRetry.StuckAfter);
        Assert.Equal(PilotApplicationChangeOutcome.Changed, await service.DeleteOnRequestAsync(id, AdminId, Ct));
    }

    [Fact]
    public async Task AFailedDeletionOnRequest_IsRecordedAndThrown_AndTheApplicationStays()
    {
        Guid id;
        await using (var db = _central.CreateContext())
            id = await PilotRetentionTestData.AddPilotAsync(db, Now.AddDays(-1), ct: Ct);

        await using (var failing = _central.CreateContextWith(new FailDeleteOf(id)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(failing).DeleteOnRequestAsync(id, AdminId, Ct));

        await using var check = _central.CreateContext();
        Assert.True(await check.ContactMessages.AnyAsync(m => m.Id == id, Ct));
        var record = await check.DataDestructionRecords.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((DataDestructionResult.Failed, DataDestructionMethod.DataSubjectRequest, (Guid?)AdminId), (record.Result, record.Method, record.ActorUserId));
    }

    private async Task<(PilotApplicationStatus?, DateTime?)> StateAsync(Guid id)
    {
        await using var db = _central.CreateContext();
        var row = await db.ContactMessages.AsNoTracking().SingleAsync(m => m.Id == id, Ct);
        return (row.PilotStatus, row.PilotLastActivityAtUtc);
    }

    private static CentralAdminPilotApplicationService Service(
        CentralDbContext db,
        TimeProvider? clock = null,
        ILogger<CentralAdminPilotApplicationService>? logger = null) =>
        new(db, new TurkishProvinceCatalog(db), clock ?? new ManualClock(new DateTimeOffset(Now)), logger ?? NullLogger<CentralAdminPilotApplicationService>.Instance);
}
