using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wasla.UnitTests.Auth;
using Wasla.Web.Realtime;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-103: what Wasla.Web does with <c>Realtime:Internal</c> at startup. Without its listener (missing or disabled) it
/// starts, logs a warning that names the key, and keeps the Live Screen at its normal polling rate while connected,
/// because no Wasla.Worker or Wasla.Api change can be pushed. With the listener the connected rate is the lower 30
/// seconds. Incomplete and invalid configuration stops Web as it stops the senders
/// (<see cref="InternalRealtimeStartupPolicyTests"/>), and no message or log line carries the secret.
/// </summary>
public sealed class WebInternalListenerStartupTests
{
    [Theory]
    [MemberData(nameof(InternalRealtimeStartupPolicyTests.IncompleteOrInvalid), MemberType = typeof(InternalRealtimeStartupPolicyTests))]
    public void IncompleteOrInvalidConfiguration_StopsWeb_WithoutTheSecret(string environment, string json, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BuildWeb(environment, InternalRealtimeStartupPolicyTests.Section(json), new CapturingLoggerProvider()));

        InternalRealtimeStartupPolicyTests.AssertNamesKeyWithoutSecret(error, expected);
    }

    public static TheoryData<string, string> WebWithoutListener() => new()
    {
        { Environments.Production, "{}" },
        { Environments.Development, "{}" },
        { Environments.Production, "{\"Disabled\":\"true\"}" }
    };

    [Theory]
    [MemberData(nameof(WebWithoutListener))]
    public void Web_WithoutItsListener_StartsWithAWarning_AndKeepsTheConnectedLiveScreenAtTheNormalRate(string environment, string json)
    {
        var logs = new CapturingLoggerProvider();
        using var web = BuildWeb(environment, InternalRealtimeStartupPolicyTests.Section(json), logs);
        web.Services.LogInternalListenerState(web.Services.GetRequiredService<IConfiguration>());

        var polling = web.Services.GetRequiredService<LiveScreenPolling>();
        Assert.Equal(10000, polling.IntervalMs);
        Assert.Equal(10000, polling.ConnectedIntervalMs);
        Assert.Null(web.Services.GetService<InternalTenantChangeListener>());

        var warning = Assert.Single(logs.Entries, e => e.StartsWith("Warning Wasla.Realtime:", StringComparison.Ordinal));
        Assert.Contains("Realtime:Internal", warning, StringComparison.Ordinal);
        Assert.Contains("10 seconds", warning, StringComparison.Ordinal);
        Assert.Contains(json.Contains("Disabled", StringComparison.Ordinal) ? "Realtime:Internal:Disabled is true" : "Realtime:Internal:Port is not set", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_WithItsListener_LowersTheConnectedRate_AndLogsNoWarningOrSecret()
    {
        var logs = new CapturingLoggerProvider();
        using var web = BuildWeb(Environments.Production, InternalRealtimeStartupPolicyTests.Channel(), logs);
        web.Services.LogInternalListenerState(web.Services.GetRequiredService<IConfiguration>());

        var polling = web.Services.GetRequiredService<LiveScreenPolling>();
        Assert.Equal(10000, polling.IntervalMs);
        Assert.Equal(30000, polling.ConnectedIntervalMs);
        Assert.NotNull(web.Services.GetService<InternalTenantChangeListener>());
        Assert.DoesNotContain(logs.Entries, e => e.StartsWith("Warning", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Contains(InternalRealtimeStartupPolicyTests.FakeSecret, StringComparison.Ordinal));
    }

    // As in Wasla.Web Program.cs: AddWaslaRealtime, then the startup log after Build.
    private static IHost BuildWeb(string environment, Dictionary<string, string?> settings, CapturingLoggerProvider logs)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment, DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddWaslaRealtime(builder.Configuration);
        return builder.Build();
    }
}
