using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.UnitTests.Platform;

/// <summary>The stage pilot fails closed: off by default, and refused when enabled half-configured or off the stage API.</summary>
public sealed class TrendyolGoMealWebhookPilotOptionsTests
{
    private static Dictionary<string, string?> Complete() => new()
    {
        ["TrendyolGoMealWebhookPilot:Enabled"] = "true",
        ["TrendyolGoMealWebhookPilot:TenantId"] = Guid.NewGuid().ToString(),
        ["TrendyolGoMealWebhookPilot:PlatformConnectionId"] = Guid.NewGuid().ToString(),
        ["TrendyolGoMealWebhookPilot:SellerId"] = "107385",
        ["TrendyolGoMealWebhookPilot:StoreId"] = "1331",
        ["TrendyolGoMealWebhookPilot:SecretSha256"] = new string('a', 64),
        ["Platform:TrendyolGo:BaseUrl"] = "https://stageapi.tgoapis.com"
    };

    private static TrendyolGoMealWebhookPilotOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<TrendyolGoOptions>(configuration.GetSection(TrendyolGoOptions.SectionName));
        services.AddTrendyolGoMealWebhookPilot(configuration);
        return services.BuildServiceProvider().GetRequiredService<IOptions<TrendyolGoMealWebhookPilotOptions>>().Value;
    }

    [Fact]
    public void NotConfigured_IsOff()
    {
        Assert.False(Resolve([]).Enabled);
    }

    [Fact]
    public void CompleteStageConfiguration_IsAccepted()
    {
        Assert.True(Resolve(Complete()).Enabled);
    }

    [Theory]
    [InlineData("TrendyolGoMealWebhookPilot:TenantId", null, "TenantId")]
    [InlineData("TrendyolGoMealWebhookPilot:PlatformConnectionId", null, "PlatformConnectionId")]
    [InlineData("TrendyolGoMealWebhookPilot:SellerId", "107385,107386", "SellerId")]
    [InlineData("TrendyolGoMealWebhookPilot:StoreId", "", "StoreId")]
    [InlineData("TrendyolGoMealWebhookPilot:SecretSha256", "the-secret-itself", "SecretSha256")]
    [InlineData("TrendyolGoMealWebhookPilot:HeaderName", "X Header", "HeaderName")]
    [InlineData("Platform:TrendyolGo:BaseUrl", "https://api.tgoapis.com", "stageapi.tgoapis.com")]
    [InlineData("Platform:TrendyolGo:BaseUrl", "http://stageapi.tgoapis.com", "stageapi.tgoapis.com")]
    public void EnabledButIncompleteAmbiguousOrNotStage_IsRefused(string key, string? value, string named)
    {
        var values = Complete();
        if (value is null)
            values.Remove(key);
        else
            values[key] = value;

        var error = Assert.Throws<OptionsValidationException>(() => Resolve(values));

        Assert.Contains(named, error.Message, StringComparison.Ordinal);
    }
}
