using Wasla.Web.Reports.Export;

namespace Wasla.UnitTests.Reports;

/// <summary>The export gate (WAS-184): one export per user of a tenant, a bounded number per process, nothing queued.</summary>
public sealed class ReportExportGateTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid User1 = Guid.NewGuid();
    private static readonly Guid User2 = Guid.NewGuid();

    [Fact]
    public void ASecondExportOfTheSameUser_IsRefused_UntilTheFirstEnds()
    {
        using var gate = new ReportExportGate();

        Assert.Equal(ReportExportAdmission.Admitted, gate.TryEnter(TenantA, User1, out var first));
        Assert.Equal(ReportExportAdmission.AlreadyRunning, gate.TryEnter(TenantA, User1, out var refused));
        Assert.Null(refused);

        first!.Dispose();
        Assert.Equal(ReportExportAdmission.Admitted, gate.TryEnter(TenantA, User1, out var again));
        again!.Dispose();
    }

    [Fact]
    public void OtherUsersAndTenants_AreNotHeldUpByOneUser()
    {
        using var gate = new ReportExportGate();

        Assert.Equal(ReportExportAdmission.Admitted, gate.TryEnter(TenantA, User1, out var a1));
        Assert.Equal(ReportExportAdmission.Admitted, gate.TryEnter(TenantA, User2, out var a2));
        Assert.Equal(ReportExportAdmission.Admitted, gate.TryEnter(TenantB, User1, out var b1));
        a1!.Dispose();
        a2!.Dispose();
        b1!.Dispose();
    }

    [Fact]
    public void TheProcessLimit_RefusesMore_AndAReleasedPlaceIsReused()
    {
        using var gate = new ReportExportGate(2);

        Assert.Equal(ReportExportAdmission.Admitted, gate.TryEnter(TenantA, User1, out var first));
        Assert.Equal(ReportExportAdmission.Admitted, gate.TryEnter(TenantA, User2, out var second));
        Assert.Equal(ReportExportAdmission.Busy, gate.TryEnter(TenantB, User1, out var third));
        Assert.Null(third);

        // A refusal for the process limit does not leave the user marked as running.
        second!.Dispose();
        second.Dispose();
        Assert.Equal(ReportExportAdmission.Admitted, gate.TryEnter(TenantB, User1, out var fourth));
        Assert.Equal(ReportExportAdmission.Busy, gate.TryEnter(TenantB, User2, out _));
        first!.Dispose();
        fourth!.Dispose();
    }

    [Fact]
    public async Task ConcurrentAttempts_OfOneUser_AdmitExactlyOne()
    {
        using var gate = new ReportExportGate(8);
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            return gate.TryEnter(TenantA, User1, out _);
        })).ToList();

        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r == ReportExportAdmission.Admitted));
        Assert.Equal(15, results.Count(r => r == ReportExportAdmission.AlreadyRunning));
    }
}
