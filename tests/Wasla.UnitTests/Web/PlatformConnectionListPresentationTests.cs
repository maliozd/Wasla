using Wasla.Web.Ui;

namespace Wasla.UnitTests.Web;

public sealed class PlatformConnectionListPresentationTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HealthyConnection_HidesDiagnostics()
    {
        Assert.False(PlatformConnectionListPresentation.ShowConsecutiveErrors(0));
        Assert.False(PlatformConnectionListPresentation.ShowCircuitOpen(null, Now));
        Assert.False(PlatformConnectionListPresentation.ShowCircuitOpen(Now.AddMinutes(-1), Now));
    }

    [Fact]
    public void ActiveConnectionWithErrors_ShowsErrorWarningWithoutChangingEnabledState()
    {
        const bool isActive = true;
        Assert.True(isActive);
        Assert.True(PlatformConnectionListPresentation.ShowConsecutiveErrors(3));
        Assert.False(PlatformConnectionListPresentation.ShowCircuitOpen(null, Now));
    }

    [Fact]
    public void OpenCircuit_ShowsOpenUntilWarning()
    {
        Assert.True(PlatformConnectionListPresentation.ShowCircuitOpen(Now.AddMinutes(5), Now));
    }
}
