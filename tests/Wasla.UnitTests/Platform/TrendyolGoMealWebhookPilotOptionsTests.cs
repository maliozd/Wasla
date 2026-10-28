using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The stage pilot fails closed: off by default (in code and in every committed settings file), and refused when enabled
/// half-configured, with a receiver URL that is not exact, or with a provider API override that is anything but exactly
/// the stage API. The deployment's own Trendyol GO API (production) is no reason to refuse it: only the pilot's connection
/// is routed to the stage API.
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
        ["TrendyolGoMealWebhookPilot:ProviderApiBaseUrl"] = "https://stageapi.tgoapis.com",
        ["Platform:TrendyolGo:BaseUrl"] = "https://api.tgoapis.com"
    };

    private static Dictionary<string, string?> OverrideOnly() => new()
    {
        ["TrendyolGoMealWebhookPilot:PlatformConnectionId"] = Guid.NewGuid().ToString(),
        ["TrendyolGoMealWebhookPilot:SellerId"] = "107385",
        ["TrendyolGoMealWebhookPilot:StoreId"] = "1331",
        ["TrendyolGoMealWebhookPilot:ProviderApiBaseUrl"] = "https://stageapi.tgoapis.com",
        ["Platform:TrendyolGo:BaseUrl"] = "https://api.tgoapis.com"
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
    public void CompleteStageConfiguration_IsAccepted_BesideTheProductionApi()
    {
        var options = Resolve(Complete());

        Assert.True(options.Enabled);
        Assert.Equal("https://stageapi.tgoapis.com", options.ProviderApiBaseUrl);
    }

    [Fact]
    public void ProviderApiOverride_WithThePilotOff_IsAccepted()
    {
        var options = Resolve(OverrideOnly());

        Assert.False(options.Enabled);
        Assert.Equal("https://stageapi.tgoapis.com", options.ProviderApiBaseUrl);
    }

    [Theory]
    [InlineData("TrendyolGoMealWebhookPilot:PlatformConnectionId", null, "PlatformConnectionId")]
    [InlineData("TrendyolGoMealWebhookPilot:SellerId", null, "SellerId")]
    [InlineData("TrendyolGoMealWebhookPilot:StoreId", "13a1", "StoreId")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "https://api.tgoapis.com", "ProviderApiBaseUrl")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", " ", "ProviderApiBaseUrl")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "https://stageapi.tgoapis.com.evil.example", "ProviderApiBaseUrl")]
    public void ProviderApiOverride_WithThePilotOff_ThatIsNotExactlyStageForOneConnection_IsRefused(string key, string? value, string named)
    {
        var values = OverrideOnly();
        if (value is null)
            values.Remove(key);
        else
            values[key] = value;

        var error = Assert.Throws<OptionsValidationException>(() => Resolve(values));

        Assert.Contains(named, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderApiOverride_IsBoundAndValidated_ByTheInfrastructureEveryHostUses()
    {
        // Web registers only AddWaslaInfrastructure; it calls Trendyol GO too (order actions), so it routes the same way.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaInfrastructure(Configuration(OverrideOnly()));
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<TrendyolGoMealWebhookPilotOptions>>().Value;
        Assert.Equal("https://stageapi.tgoapis.com", options.ProviderApiBaseUrl);

        var invalid = OverrideOnly();
        invalid["TrendyolGoMealWebhookPilot:ProviderApiBaseUrl"] = "https://api.tgoapis.com";
        var refusing = new ServiceCollection();
        refusing.AddLogging();
        refusing.AddWaslaInfrastructure(Configuration(invalid));
        Assert.Throws<OptionsValidationException>(() =>
            refusing.BuildServiceProvider().GetRequiredService<IOptions<TrendyolGoMealWebhookPilotOptions>>().Value);
    }

    [Fact]
    public void InfrastructureAndPilot_RegisterTheOptionsOnce()
    {
        var configuration = Configuration(Complete());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaInfrastructure(configuration);
        services.AddTrendyolGoMealWebhookPilot(configuration);

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<TrendyolGoMealWebhookPilotOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(IConfigureOptions<TrendyolGoMealWebhookPilotOptions>));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        values["Platforms:ProviderMode"] = "Real";
        values["ConnectionStrings:CentralDb"] = "Server=unused;Database=unused";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData("TrendyolGoMealWebhookPilot:TenantId", null, "TenantId")]
    [InlineData("TrendyolGoMealWebhookPilot:PlatformConnectionId", null, "PlatformConnectionId")]
    [InlineData("TrendyolGoMealWebhookPilot:SellerId", "107385,107386", "SellerId")]
    [InlineData("TrendyolGoMealWebhookPilot:StoreId", "", "StoreId")]
    [InlineData("TrendyolGoMealWebhookPilot:SecretSha256", "the-secret-itself", "SecretSha256")]
    [InlineData("TrendyolGoMealWebhookPilot:HeaderName", "X Header", "HeaderName")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", null, "ProviderApiBaseUrl")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "https://api.tgoapis.com", "stageapi.tgoapis.com")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "http://stageapi.tgoapis.com", "stageapi.tgoapis.com")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "https://stageapi.tgoapis.com/other", "stageapi.tgoapis.com")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "https://stageapi.tgoapis.com?x=1", "stageapi.tgoapis.com")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "https://stageapi.tgoapis.com#x", "stageapi.tgoapis.com")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "https://u:p@stageapi.tgoapis.com", "stageapi.tgoapis.com")]
    [InlineData("TrendyolGoMealWebhookPilot:ProviderApiBaseUrl", "https://stageapi.tgoapis.com:8443", "stageapi.tgoapis.com")]
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
