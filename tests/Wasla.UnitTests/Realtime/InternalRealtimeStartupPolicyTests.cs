using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Infrastructure.Realtime;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-103: what Wasla.Worker and Wasla.Api do with <c>Realtime:Internal</c> at startup. Outside Development they refuse
/// to start without the channel unless the environment is explicitly polling-only; Development starts as before.
/// Incomplete and invalid configuration is refused everywhere. Each state logs differently, and no message, exception
/// or log line carries the secret. The hosts are built the way Wasla.Worker's Program.cs builds its host. Wasla.Web's
/// side is covered by <c>WebInternalListenerStartupTests</c>.
/// </summary>
public sealed class InternalRealtimeStartupPolicyTests
{
    // Obviously fake. Asserted absent from every exception and log line.
    internal const string FakeSecret = "FAKE-TEST-SECRET-never-log-me-0123456789abcdef";

    public static TheoryData<string> NonDevelopmentEnvironments => new() { Environments.Production, Environments.Staging, "Pilot" };

    public static TheoryData<string> AllEnvironments => new() { Environments.Development, Environments.Production, Environments.Staging };

    [Theory]
    [MemberData(nameof(NonDevelopmentEnvironments))]
    public void NonDevelopmentSender_WithoutTheChannel_RefusesToStart_AndSaysHowToFixIt(string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BuildSender(environment, new Dictionary<string, string?>()));

        Assert.Contains("Realtime:Internal is not configured", error.Message, StringComparison.Ordinal);
        Assert.Contains("Realtime:Internal:Port", error.Message, StringComparison.Ordinal);
        Assert.Contains("Realtime:Internal:Secret", error.Message, StringComparison.Ordinal);
        Assert.Contains("Realtime:Internal:Disabled", error.Message, StringComparison.Ordinal);
        Assert.Contains(environment, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonDevelopmentEnvironments))]
    public void NonDevelopmentSender_ExplicitlyPollingOnly_Starts_AndDropsNotifications(string environment)
    {
        using var host = BuildSender(environment, new Dictionary<string, string?> { ["Realtime:Internal:Disabled"] = "true" });

        Assert.IsNotType<WebTenantChangeNotifier>(host.Services.GetRequiredService<ITenantChangeNotifier>());
        Assert.Equal(InternalRealtimeState.Disabled, InternalRealtimeOptions.Read(host.Services.GetRequiredService<IConfiguration>()).State);
    }

    [Fact]
    public void DevelopmentSender_WithoutTheChannel_StartsAsBefore()
    {
        using var host = BuildSender(Environments.Development, new Dictionary<string, string?>());

        Assert.IsNotType<WebTenantChangeNotifier>(host.Services.GetRequiredService<ITenantChangeNotifier>());
    }

    [Theory]
    [MemberData(nameof(AllEnvironments))]
    public void Sender_WithTheChannel_SendsToWeb(string environment)
    {
        using var host = BuildSender(environment, Channel());

        Assert.IsType<WebTenantChangeNotifier>(host.Services.GetRequiredService<ITenantChangeNotifier>());
    }

    public static TheoryData<string, string, string> IncompleteOrInvalid()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var environment in new[] { Environments.Development, Environments.Production })
        {
            // Existing validation (WAS-23), unchanged.
            data.Add(environment, "{\"Port\":\"5290\"}", "Realtime:Internal:Secret must be set");
            data.Add(environment, "{\"Port\":\"5290\",\"Secret\":\"FAKE-short\"}", "Realtime:Internal:Secret must be set");
            data.Add(environment, "{\"Port\":\"5290\",\"Secret\":\"" + FakeSecret + " x\"}", "printable ASCII");
            data.Add(environment, "{\"Port\":\"0\",\"Secret\":\"" + FakeSecret + "\"}", "Realtime:Internal:Port must be between");
            data.Add(environment, "{\"Port\":\"70000\",\"Secret\":\"" + FakeSecret + "\"}", "Realtime:Internal:Port must be between");
            data.Add(environment, "{\"Port\":\"5290\",\"Secret\":\"" + FakeSecret + "\",\"TimeoutSeconds\":\"30\"}", "Realtime:Internal:TimeoutSeconds");
            // New (WAS-103): a secret without a port, and an opt-out that contradicts a configured channel.
            data.Add(environment, "{\"Secret\":\"" + FakeSecret + "\"}", "Realtime:Internal:Port is not");
            data.Add(environment, "{\"Disabled\":\"true\",\"Port\":\"5290\",\"Secret\":\"" + FakeSecret + "\"}", "Realtime:Internal:Disabled is true");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(IncompleteOrInvalid))]
    public void IncompleteOrInvalidConfiguration_IsRefusedInEveryEnvironment_WithoutTheSecret(
        string environment, string json, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BuildSender(environment, Section(json)));

        AssertNamesKeyWithoutSecret(error, expected);
    }

    [Fact]
    public void Senders_LogTheChannelState_EachStateDifferently_AndNeverTheSecret()
    {
        var enabled = SenderLog(Environments.Production, Channel());
        var disabled = SenderLog(Environments.Production, new Dictionary<string, string?> { ["Realtime:Internal:Disabled"] = "true" });
        var missing = SenderLog(Environments.Development, new Dictionary<string, string?>());

        Assert.Contains(enabled, e => e.StartsWith("Information Wasla.Realtime: Wasla.Worker sends live change notifications", StringComparison.Ordinal));
        Assert.DoesNotContain(enabled, e => e.StartsWith("Warning", StringComparison.Ordinal));

        var disabledWarning = Assert.Single(disabled, e => e.StartsWith("Warning Wasla.Realtime:", StringComparison.Ordinal));
        Assert.Contains("Realtime:Internal:Disabled is true", disabledWarning, StringComparison.Ordinal);

        var missingWarning = Assert.Single(missing, e => e.StartsWith("Warning Wasla.Realtime:", StringComparison.Ordinal));
        Assert.Contains("Realtime:Internal is not configured", missingWarning, StringComparison.Ordinal);
        Assert.Contains("Development", missingWarning, StringComparison.Ordinal);
        Assert.NotEqual(disabledWarning, missingWarning);

        Assert.DoesNotContain(enabled.Concat(disabled).Concat(missing), e => e.Contains(FakeSecret, StringComparison.Ordinal));
    }

    internal static void AssertNamesKeyWithoutSecret(InvalidOperationException error, string expected)
    {
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE-short", error.ToString(), StringComparison.Ordinal);
    }

    internal static Dictionary<string, string?> Channel() => new()
    {
        ["Realtime:Internal:Port"] = InternalTenantChangeChannelTests.FreeLoopbackPort().ToString(CultureInfo.InvariantCulture),
        ["Realtime:Internal:Secret"] = FakeSecret
    };

    internal static Dictionary<string, string?> Section(string json)
    {
        var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        return values.ToDictionary(v => $"Realtime:Internal:{v.Key}", v => (string?)v.Value);
    }

    // As in Wasla.Worker Program.cs (Wasla.Api calls the same registration with its own builder's environment). No
    // environment variables or appsettings files are read, so a developer machine's own channel settings cannot leak in.
    private static IHost BuildSender(string environment, Dictionary<string, string?> settings, ILoggerProvider? logs = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment, DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        if (logs is not null)
            builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ITenantChangeNotifier>(NullTenantChangeNotifier.Instance);
        builder.Services.AddWaslaWebTenantChangeNotifier(builder.Configuration, builder.Environment);
        return builder.Build();
    }

    private static IReadOnlyList<string> SenderLog(string environment, Dictionary<string, string?> settings)
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildSender(environment, settings, logs);
        host.Services.LogWebTenantChangeChannel(host.Services.GetRequiredService<IConfiguration>(), "Wasla.Worker");
        return logs.Entries;
    }
}
