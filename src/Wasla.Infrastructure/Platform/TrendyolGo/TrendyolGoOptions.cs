namespace Wasla.Infrastructure.Platform.TrendyolGo;

public sealed class TrendyolGoOptions
{
    public const string SectionName = "Platform:TrendyolGo";
    public string BaseUrl { get; set; } = "https://stageapi.tgoapis.com";
    public string AgentName { get; set; } = "OrderHub";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

