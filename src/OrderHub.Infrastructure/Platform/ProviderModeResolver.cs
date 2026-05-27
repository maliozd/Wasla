using Microsoft.Extensions.Configuration;

namespace OrderHub.Infrastructure.Platform;

public enum PlatformProviderMode
{
    Mock,
    Real
}

public sealed record ProviderModeResult(PlatformProviderMode Mode)
{
    public bool IsMock => Mode == PlatformProviderMode.Mock;
    public string ModeLabel => Mode.ToString();
}

public static class ProviderModeResolver
{
    private const string ConfigKey = "Platforms:ProviderMode";

    public static ProviderModeResult Resolve(IConfiguration configuration)
    {
        var raw = configuration[ConfigKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is required. Allowed values: Mock, Real.");
        }

        var value = raw.Trim();

        if (string.Equals(value, "Mock", StringComparison.OrdinalIgnoreCase))
            return new ProviderModeResult(PlatformProviderMode.Mock);

        if (string.Equals(value, "Real", StringComparison.OrdinalIgnoreCase))
            return new ProviderModeResult(PlatformProviderMode.Real);

        throw new InvalidOperationException(
            $"Invalid {ConfigKey} value '{value}'. Allowed values: Mock, Real.");
    }
}
