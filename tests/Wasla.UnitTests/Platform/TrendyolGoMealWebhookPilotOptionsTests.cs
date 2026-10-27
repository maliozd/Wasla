using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The stage pilot fails closed: off by default (in code and in every committed settings file), and refused when enabled
/// half-configured, with a receiver URL that is not exact, or with anything but exactly the stage API.
/// </summary>
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
        ["TrendyolGoMealWebhookPilot:ReceiverBaseUrl"] = "https://app.wasla.example/api/webhooks/trendyol-go/meal",
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
    public void EnabledFlagAlone_IsRefused()
    {
        var error = Assert.Throws<OptionsValidationException>(() => Resolve(new() { ["TrendyolGoMealWebhookPilot:Enabled"] = "true" }));

        Assert.Contains("TenantId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommittedSettingsFile_EnablesThePilot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Wasla.sln")))
            root = root.Parent;
        Assert.NotNull(root);

        var files = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (json.RootElement.TryGetProperty(TrendyolGoMealWebhookPilotOptions.SectionName, out var section)
                && section.TryGetProperty("Enabled", out var enabled))
            {
                Assert.False(enabled.ValueKind == JsonValueKind.True, $"{file} enables the pilot.");
            }
        }
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
    [InlineData("Platform:TrendyolGo:BaseUrl", "https://stageapi.tgoapis.com/other", "stageapi.tgoapis.com")]
    [InlineData("Platform:TrendyolGo:BaseUrl", "https://stageapi.tgoapis.com?x=1", "stageapi.tgoapis.com")]
    [InlineData("Platform:TrendyolGo:BaseUrl", "https://stageapi.tgoapis.com#x", "stageapi.tgoapis.com")]
    [InlineData("Platform:TrendyolGo:BaseUrl", "https://u:p@stageapi.tgoapis.com", "stageapi.tgoapis.com")]
    [InlineData("Platform:TrendyolGo:BaseUrl", "https://stageapi.tgoapis.com:8443", "stageapi.tgoapis.com")]
    [InlineData("TrendyolGoMealWebhookPilot:ReceiverBaseUrl", null, "ReceiverBaseUrl")]
    [InlineData("TrendyolGoMealWebhookPilot:ReceiverBaseUrl", "http://app.wasla.example/api/webhooks/trendyol-go/meal", "ReceiverBaseUrl")]
    [InlineData("TrendyolGoMealWebhookPilot:ReceiverBaseUrl", "https://app.wasla.example/api/webhooks/trendyol-go/meal#f", "ReceiverBaseUrl")]
    [InlineData("TrendyolGoMealWebhookPilot:ReceiverBaseUrl", "https://app.wasla.example/api/webhooks/trendyol-go/meal?key=1", "ReceiverBaseUrl")]
    [InlineData("TrendyolGoMealWebhookPilot:ReceiverBaseUrl", "https://u@app.wasla.example/api/webhooks/trendyol-go/meal", "ReceiverBaseUrl")]
    [InlineData("TrendyolGoMealWebhookPilot:ReceiverBaseUrl", "https://app.wasla.example/hooks", "ReceiverBaseUrl")]
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
