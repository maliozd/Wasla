namespace OrderHub.PrintBridge.Options;

public sealed class OrderHubOptions
{
    public const string SectionName = "OrderHub";

    public string BaseUrl { get; set; } = string.Empty;

    public string AgentToken { get; set; } = string.Empty;
}
