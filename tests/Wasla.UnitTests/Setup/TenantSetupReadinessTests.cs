using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Setup;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Setup;

public sealed class TenantSetupReadinessTests
{
    private static readonly DateTime UtcNow = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Restaurant_CompleteProfile_IsCompleteButOptional()
    {
        var status = Evaluate(Restaurant("Mengen Lokantası", "5320000000", "İstanbul", "Türkiye"));

        Assert.True(status.Restaurant.IsComplete);
        Assert.True(status.Restaurant.IsOptional);
        Assert.False(status.Restaurant.CountsTowardReadiness);
    }

    [Theory]
    [InlineData("", "5320000000", "İstanbul", "Türkiye")]
    [InlineData("   ", "5320000000", "İstanbul", "Türkiye")]
    [InlineData("Mengen Lokantası", "", "İstanbul", "Türkiye")]
    [InlineData("Mengen Lokantası", "5320000000", "", "")]
    [InlineData("Mengen Lokantası", "5320000000", "   ", null)]
    public void Restaurant_MissingOperationalField_IsIncompleteWithoutBlockingReadiness(
        string name,
        string phone,
        string city,
        string? country)
    {
        var status = Evaluate(Restaurant(name, phone, city, country));

        Assert.False(status.Restaurant.IsComplete);
        Assert.True(status.IsReady);
        Assert.Equal(2, status.RequiredStepsCompleted);
        Assert.Equal(2, status.RequiredStepsTotal);
    }

    [Fact]
    public void Restaurant_CountryWithoutCity_IsComplete()
    {
        var status = Evaluate(Restaurant("Mengen Lokantası", "5320000000", "", "Türkiye"));

        Assert.True(status.Restaurant.IsComplete);
    }

    [Fact]
    public void Restaurant_UnavailableSource_DoesNotClaimSuccess()
    {
        var facts = ReadyFacts() with
        {
            Restaurant = new RestaurantProfileFacts(false, "Mengen Lokantası", "5320000000", "İstanbul", "Türkiye")
        };

        var status = TenantSetupReadiness.Evaluate(facts, UtcNow);

        Assert.False(status.Restaurant.IsAvailable);
        Assert.False(status.Restaurant.IsComplete);
        Assert.True(status.IsReady);
    }

    [Fact]
    public void Platforms_NoneConnected_IsIncomplete()
    {
        var status = Evaluate(Platforms());

        Assert.False(status.Platform.IsComplete);
        Assert.Empty(status.ActivePlatforms);
        Assert.False(status.IsReady);
    }

    [Fact]
    public void Platforms_InactiveOnly_IsIncomplete()
    {
        var status = Evaluate(Platforms(
            Connection(FoodPlatform.Yemeksepeti, isActive: false)));

        Assert.False(status.Platform.IsComplete);
        Assert.Empty(status.ActivePlatforms);
    }

    [Fact]
    public void Platforms_ActiveWithoutStoreOrCredentials_IsIncomplete()
    {
        var status = Evaluate(Platforms(
            Connection(FoodPlatform.GetirYemek, storeId: " ", hasApiKey: false, hasApiSecret: true),
            Connection(FoodPlatform.TrendyolYemek, storeId: "store-1", hasApiKey: true, hasApiSecret: false)));

        Assert.False(status.Platform.IsComplete);
    }

    [Fact]
    public void Platforms_AtLeastOneActive_IsComplete()
    {
        var status = Evaluate(Platforms(
            Connection(FoodPlatform.Yemeksepeti, isActive: false),
            Connection(FoodPlatform.GetirYemek)));

        Assert.True(status.Platform.IsComplete);
        Assert.Equal([FoodPlatform.GetirYemek], status.ActivePlatforms);
    }

    [Fact]
    public void Platforms_MultipleActive_AreCounted()
    {
        var status = Evaluate(Platforms(
            Connection(FoodPlatform.TrendyolYemek),
            Connection(FoodPlatform.Yemeksepeti),
            Connection(FoodPlatform.GetirYemek)));

        Assert.True(status.Platform.IsComplete);
        Assert.Equal(3, status.ActivePlatforms.Count);
    }

    [Fact]
    public void Platforms_UnavailableSource_DoesNotClaimSuccess()
    {
        var facts = ReadyFacts() with
        {
            Platforms = new PlatformConnectionFacts(false, [Connection(FoodPlatform.Yemeksepeti)])
        };

        var status = TenantSetupReadiness.Evaluate(facts, UtcNow);

        Assert.False(status.Platform.IsComplete);
        Assert.False(status.IsReady);
    }

    [Fact]
    public void Notifications_EffectiveSoundOn_IsCompleteButOptional()
    {
        var status = Evaluate(Notifications(soundEnabled: true));

        Assert.True(status.Notifications.IsComplete);
        Assert.True(status.Notifications.IsOptional);
        Assert.False(status.Notifications.CountsTowardReadiness);
    }

    [Fact]
    public void Notifications_SoundOff_DoesNotBlockReadiness()
    {
        var status = Evaluate(Notifications(soundEnabled: false));

        Assert.False(status.Notifications.IsComplete);
        Assert.True(status.IsReady);
        Assert.Equal(2, status.RequiredStepsCompleted);
        Assert.Equal(2, status.RequiredStepsTotal);
    }

    [Fact]
    public void Notifications_UnavailableSource_DoesNotClaimSuccessOrBlockReadiness()
    {
        var status = Evaluate(new NotificationSetupFacts(false, SoundEnabled: true));

        Assert.False(status.Notifications.IsAvailable);
        Assert.False(status.Notifications.IsComplete);
        Assert.True(status.IsReady);
    }

    [Fact]
    public void Printing_Absent_IsRequiredAndBlocksReadiness()
    {
        var status = Evaluate(new PrintingSetupFacts(true, []));

        Assert.False(status.Printing.IsOptional);
        Assert.True(status.Printing.CountsTowardReadiness);
        Assert.False(status.Printing.IsComplete);
        Assert.False(status.IsReady);
        Assert.Equal(1, status.RequiredStepsCompleted);
        Assert.Equal(2, status.RequiredStepsTotal);
    }

    [Theory]
    [InlineData(true, -30, true)]
    [InlineData(true, -180, false)]
    [InlineData(true, -600, false)]
    [InlineData(false, -10, false)]
    public void Printing_UsesHeartbeatStatus(bool isActive, int lastSeenOffsetSeconds, bool expectedComplete)
    {
        var device = new PrintingDeviceFact(isActive, UtcNow.AddSeconds(lastSeenOffsetSeconds));
        var facts = ReadyFacts() with
        {
            Printing = new PrintingSetupFacts(true, [device])
        };

        var status = TenantSetupReadiness.Evaluate(facts, UtcNow);

        Assert.Equal(expectedComplete, status.Printing.IsComplete);
        Assert.Equal(expectedComplete, status.IsReady);
    }

    [Fact]
    public void Printing_NeverConnectedToken_IsNotComplete()
    {
        var facts = ReadyFacts() with
        {
            Printing = new PrintingSetupFacts(true, [new PrintingDeviceFact(true, null)])
        };

        var status = TenantSetupReadiness.Evaluate(facts, UtcNow);

        Assert.False(status.Printing.IsComplete);
        Assert.Equal(
            PrintBridgeConnectionStatus.NeverConnected,
            PrintBridgeConnectionStatusCalculator.Calculate(true, null, UtcNow));
    }

    [Fact]
    public void Printing_UnavailableSource_DoesNotClaimSuccessOrReadiness()
    {
        var facts = ReadyFacts() with
        {
            Printing = new PrintingSetupFacts(false, [new PrintingDeviceFact(true, UtcNow)])
        };

        var status = TenantSetupReadiness.Evaluate(facts, UtcNow);

        Assert.False(status.Printing.IsAvailable);
        Assert.False(status.Printing.IsComplete);
        Assert.False(status.IsReady);
    }

    [Fact]
    public void Readiness_PlatformAndPrintBridge_AreTheOnlyRequiredSteps()
    {
        var status = TenantSetupReadiness.Evaluate(ReadyFacts(), UtcNow);

        Assert.True(status.IsReady);
        Assert.Equal(2, status.RequiredStepsCompleted);
        Assert.Equal(2, status.RequiredStepsTotal);
        Assert.True(status.Platform.CountsTowardReadiness);
        Assert.True(status.Printing.CountsTowardReadiness);
        Assert.False(status.Restaurant.CountsTowardReadiness);
        Assert.False(status.Notifications.CountsTowardReadiness);
        Assert.False(status.LiveScreen.IsComplete);
        Assert.False(status.LiveScreen.CountsTowardReadiness);
    }

    [Fact]
    public void Readiness_OnlyRequiredSteps_IsReadyWithoutProfileOrNotifications()
    {
        // The 18 Oct 2026 review: platform and Print Bridge connected, sound off and an incomplete profile.
        // That is ready (2/2), not 2/3.
        var facts = ReadyFacts() with
        {
            Restaurant = Restaurant("Mengen Lokantası", "", "", null),
            Notifications = Notifications(soundEnabled: false)
        };

        var status = TenantSetupReadiness.Evaluate(facts, UtcNow);

        Assert.True(status.IsReady);
        Assert.Equal(2, status.RequiredStepsCompleted);
        Assert.Equal(2, status.RequiredStepsTotal);
    }

    [Fact]
    public void Readiness_NothingConnected_IsZeroOfTwo()
    {
        var facts = ReadyFacts() with
        {
            Platforms = new PlatformConnectionFacts(true, []),
            Printing = new PrintingSetupFacts(true, [])
        };

        var status = TenantSetupReadiness.Evaluate(facts, UtcNow);

        Assert.False(status.IsReady);
        Assert.Equal(0, status.RequiredStepsCompleted);
        Assert.Equal(2, status.RequiredStepsTotal);
    }

    [Fact]
    public void Readiness_MissingPlatform_IsNotReady()
    {
        var facts = ReadyFacts() with
        {
            Platforms = new PlatformConnectionFacts(true, [])
        };

        var status = TenantSetupReadiness.Evaluate(facts, UtcNow);

        Assert.False(status.IsReady);
        Assert.Equal(1, status.RequiredStepsCompleted);
        Assert.Equal(2, status.RequiredStepsTotal);
    }

    [Fact]
    public void GuidanceCompletion_DoesNotChangeReadiness()
    {
        var completed = ReadyFacts() with { SetupGuidanceCompleted = true };
        var ready = TenantSetupReadiness.Evaluate(completed, UtcNow);
        var later = TenantSetupReadiness.Evaluate(
            completed with
            {
                Platforms = new PlatformConnectionFacts(true, [])
            },
            UtcNow);

        Assert.True(ready.IsReady);
        Assert.True(ready.IsSetupGuidanceCompleted);
        Assert.False(later.IsReady);
        Assert.True(later.IsSetupGuidanceCompleted);
    }

    [Fact]
    public void Team_OnlyInitialOwner_HasNoAdditionalMember()
    {
        var owner = Member(UserRole.Owner, active: true, createdAt: UtcNow);

        Assert.False(TenantSetupReadiness.HasAdditionalActiveTeamMember([owner]));
    }

    [Fact]
    public void Team_InactiveExtraUser_DoesNotCount()
    {
        var owner = Member(UserRole.Owner, active: true, createdAt: UtcNow);
        var cashier = Member(UserRole.Cashier, active: false, createdAt: UtcNow.AddMinutes(1));

        Assert.False(TenantSetupReadiness.HasAdditionalActiveTeamMember([owner, cashier]));
    }

    [Fact]
    public void Team_ActiveExtraUser_Counts()
    {
        var owner = Member(UserRole.Owner, active: true, createdAt: UtcNow);
        var manager = Member(UserRole.Manager, active: true, createdAt: UtcNow.AddMinutes(1));

        Assert.True(TenantSetupReadiness.HasAdditionalActiveTeamMember([owner, manager]));
    }

    [Fact]
    public void Team_SecondActiveOwner_CountsAsAdditional()
    {
        var first = Member(UserRole.Owner, active: true, createdAt: UtcNow);
        var second = Member(UserRole.Owner, active: true, createdAt: UtcNow.AddDays(1));

        Assert.True(TenantSetupReadiness.HasAdditionalActiveTeamMember([second, first]));
    }

    [Fact]
    public void Readiness_BecomesFalseAgainWhenPlatformIsNoLongerActive()
    {
        var ready = TenantSetupReadiness.Evaluate(ReadyFacts(), UtcNow);
        var later = TenantSetupReadiness.Evaluate(
            ReadyFacts() with
            {
                Platforms = new PlatformConnectionFacts(
                    true,
                    [Connection(FoodPlatform.Yemeksepeti, isActive: false)])
            },
            UtcNow);

        Assert.True(ready.IsReady);
        Assert.False(later.IsReady);
        Assert.False(later.Platform.IsComplete);
    }

    private static TenantSetupStatus Evaluate(RestaurantProfileFacts restaurant) =>
        TenantSetupReadiness.Evaluate(ReadyFacts() with { Restaurant = restaurant }, UtcNow);

    private static TenantSetupStatus Evaluate(PlatformConnectionFacts platforms) =>
        TenantSetupReadiness.Evaluate(ReadyFacts() with { Platforms = platforms }, UtcNow);

    private static TenantSetupStatus Evaluate(NotificationSetupFacts notifications) =>
        TenantSetupReadiness.Evaluate(ReadyFacts() with { Notifications = notifications }, UtcNow);

    private static TenantSetupStatus Evaluate(PrintingSetupFacts printing) =>
        TenantSetupReadiness.Evaluate(ReadyFacts() with { Printing = printing }, UtcNow);

    /// <summary>Both required steps done: an active platform and a Print Bridge with a fresh heartbeat.</summary>
    private static TenantSetupFacts ReadyFacts() => new(
        Restaurant("Mengen Lokantası", "5320000000", "İstanbul", "Türkiye"),
        Platforms(Connection(FoodPlatform.Yemeksepeti)),
        Notifications(soundEnabled: true),
        new PrintingSetupFacts(true, [new PrintingDeviceFact(true, UtcNow.AddSeconds(-10))]));

    private static RestaurantProfileFacts Restaurant(string name, string phone, string city, string? country) =>
        new(true, name, phone, city, country);

    private static PlatformConnectionFacts Platforms(params PlatformConnectionFact[] connections) =>
        new(true, connections);

    private static PlatformConnectionFact Connection(
        FoodPlatform platform,
        bool isActive = true,
        string storeId = "store-1",
        bool hasApiKey = true,
        bool hasApiSecret = true) =>
        new(platform, isActive, storeId, hasApiKey, hasApiSecret);

    private static NotificationSetupFacts Notifications(bool soundEnabled) =>
        new(true, soundEnabled);

    private static TenantTeamMemberFact Member(UserRole role, bool active, DateTime createdAt) =>
        new(Guid.NewGuid(), role, active, createdAt);
}
