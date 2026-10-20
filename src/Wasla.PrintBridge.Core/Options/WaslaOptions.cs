namespace Wasla.PrintBridge.Options;

public sealed class WaslaOptions
{
    public const string SectionName = "Wasla";

    public const string DefaultServerUrl = "https://localhost:7200";

    public string ServerUrl { get; set; } = DefaultServerUrl;

    public string AgentToken { get; set; } = string.Empty;
}
