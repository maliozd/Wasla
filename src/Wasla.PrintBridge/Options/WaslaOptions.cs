namespace Wasla.PrintBridge.Options;

public sealed class WaslaOptions
{
    public const string SectionName = "OrderHub";

    public string BaseUrl { get; set; } = string.Empty;

    public string AgentToken { get; set; } = string.Empty;
}
