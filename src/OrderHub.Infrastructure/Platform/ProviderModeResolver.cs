using Microsoft.Extensions.Configuration;

namespace OrderHub.Infrastructure.Platform;

public static class ProviderModeResolver
{
    public sealed record Result(string ModeLabel, bool UseMocks);

    public static Result Resolve(IConfiguration configuration)
    {
        var providerModeRaw = configuration["Platforms:ProviderMode"];
        if (!string.IsNullOrWhiteSpace(providerModeRaw))
        {
            var mode = providerModeRaw.Trim();
            var useMocks = !string.Equals(mode, "Real", StringComparison.OrdinalIgnoreCase);
            return new Result(useMocks ? "Mock" : "Real", useMocks);
        }

        var legacyUseMocks = configuration.GetValue<bool?>("Platform:UseMocks");
        if (legacyUseMocks.HasValue)
        {
            return new Result(
                legacyUseMocks.Value ? "Mock (legacy Platform:UseMocks=true)" : "Real (legacy Platform:UseMocks=false)",
                legacyUseMocks.Value);
        }

        return new Result("Mock (default)", true);
    }
}
