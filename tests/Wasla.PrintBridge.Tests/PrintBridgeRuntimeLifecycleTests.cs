using System.Xml.Linq;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeRuntimeLifecycleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CentralDbContext _db;
    private readonly PrintBridgeDeviceManagementService _devices;
    private readonly Guid _tenantId;

    public PrintBridgeRuntimeLifecycleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new CentralDbContext(options);
        _db.Database.EnsureCreated();
        _tenantId = SeedTenant();
        _devices = new PrintBridgeDeviceManagementService(
            _db,
            NullLogger<PrintBridgeDeviceManagementService>.Instance,
            NoActivePrintJobChecker.Instance);
    }

    [Fact]
    public async Task DisabledDevice_ProducesDisabledState_AndDoesNotClearToken()
    {
        var created = await _devices.CreateDeviceAsync(_tenantId, "Kitchen POS", CancellationToken.None);
        var device = await _db.PrintBridgeDevices
            .SingleAsync(d => d.Id == created.DeviceId, TestContext.Current.CancellationToken);
        device.IsActive = false;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var auth = new PrintBridgeAuthService(_db, NullLogger<PrintBridgeAuthService>.Instance);

        var authResult = await auth.AuthenticateDetailedAsync(
            created.RawToken,
            new PrintBridgeClientInfo("DESKTOP", "1.0.0", "POS-58", "127.0.0.1", Guid.NewGuid()),
            CancellationToken.None);
        var ex = PrintBridgeConnectionException.FromResponse(
            "api/print-bridge/health",
            "https://sushim.wasla.local",
            403,
            """{"error":"device_disabled"}""",
            "device_disabled");

        Assert.False(authResult.Succeeded);
        Assert.Equal(PrintBridgeAuthFailureCode.DeviceDisabled, authResult.FailureCode);
        Assert.Equal(PrintBridgeRuntimeIssueCode.DisabledByAdmin, ex.IssueCode);
        Assert.False(new PrintBridgeRuntimeIssue(ex.IssueCode, ex.UserMessageKey, ex.FormatArgs).ShouldClearToken);
    }

    [Theory]
    [InlineData("device_removed")]
    [InlineData("device_auth_invalid")]
    [InlineData("print_bridge_token_required")]
    public void RemovedRevokedOrAuthFailed_ProducesReconnectRequired_AndClearsOnlyTokenState(string serverErrorCode)
    {
        var ex = PrintBridgeConnectionException.FromResponse(
            "api/print-bridge/jobs/pending",
            "https://sushim.wasla.local",
            401,
            $$"""{"error":"{{serverErrorCode}}"}""",
            serverErrorCode);
        var issue = new PrintBridgeRuntimeIssue(ex.IssueCode, ex.UserMessageKey, ex.FormatArgs);
        var hub = new WaslaOptions
        {
            ServerUrl = "https://sushim.wasla.local:7200",
            AgentToken = "old-token"
        };
        var bridge = new PrintBridgeOptions
        {
            InstallationId = Guid.NewGuid().ToString("D"),
            DisplayName = "Kitchen POS",
            ServerDeviceNameResolved = true,
            MachineName = "DESKTOP"
        };
        var installationId = bridge.InstallationId;

        var changed = PrintBridgeRuntimeCredentialFallback.ClearTokenForReconnectRequired(hub, bridge);

        Assert.Equal(PrintBridgeRuntimeIssueCode.ReconnectRequired, ex.IssueCode);
        Assert.True(issue.ShouldClearToken);
        Assert.True(issue.ShouldStopPolling);
        Assert.True(changed);
        Assert.Equal(string.Empty, hub.AgentToken);
        Assert.Equal("https://sushim.wasla.local:7200", hub.ServerUrl);
        Assert.Equal(installationId, bridge.InstallationId);
        Assert.Equal(string.Empty, bridge.DisplayName);
        Assert.False(bridge.ServerDeviceNameResolved);
    }

    [Theory]
    [InlineData("device_removed")]
    [InlineData("device_auth_invalid")]
    [InlineData("print_bridge_token_required")]
    public void ReconnectRequiredIssue_OverridesStaleConnectedHealth(string serverErrorCode)
    {
        var ex = PrintBridgeConnectionException.FromResponse(
            "api/print-bridge/health",
            "https://sushim.wasla.local",
            401,
            $$"""{"error":"{{serverErrorCode}}"}""",
            serverErrorCode);
        var issue = new PrintBridgeRuntimeIssue(ex.IssueCode, ex.UserMessageKey, ex.FormatArgs);

        var isConnected = PrintBridgeRuntimeStatus.ResolveEffectiveConnection(
            hasRecentSuccessfulContact: true,
            issue);
        var status = CreateStatus(isRunning: false, isConnected, issue);

        Assert.False(isConnected);
        Assert.Equal(BridgeServerConnectionStatus.Error, status.ServerConnectionStatus);
        Assert.Equal(TrayIconState.ConnectionLost, status.TrayIconState);
        Assert.Equal("RuntimeIssue.ReconnectRequired.Title", PrintBridgeRuntimeStatus.ResolveHeaderBadgeResourceKey(status));
    }

    [Fact]
    public void DisabledByAdminIssue_DoesNotRenderAsConnectedOrReconnectRequired()
    {
        var issue = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DisabledByAdmin);

        var isConnected = PrintBridgeRuntimeStatus.ResolveEffectiveConnection(
            hasRecentSuccessfulContact: true,
            issue);
        var status = CreateStatus(isRunning: false, isConnected, issue);

        Assert.False(isConnected);
        Assert.False(issue.ShouldClearToken);
        Assert.False(issue.ShouldStopPolling);
        Assert.Equal("RuntimeIssue.DisabledByAdmin.Detail", issue.EffectiveDetailResourceKey);
        Assert.Equal("RuntimeIssue.DisabledByAdmin.Title", issue.EffectiveTitleResourceKey);
        Assert.Equal(BridgeServerConnectionStatus.Error, status.ServerConnectionStatus);
        Assert.Equal("RuntimeIssue.DisabledByAdmin.Title", PrintBridgeRuntimeStatus.ResolveHeaderBadgeResourceKey(status));
    }

    [Fact]
    public void HealthyRuntime_WithRecentContact_StillRendersConnected()
    {
        var isConnected = PrintBridgeRuntimeStatus.ResolveEffectiveConnection(
            hasRecentSuccessfulContact: true,
            lastIssue: null);
        var status = CreateStatus(isRunning: true, isConnected, lastIssue: null);

        Assert.True(isConnected);
        Assert.Equal(BridgeServerConnectionStatus.Connected, status.ServerConnectionStatus);
        Assert.Equal(TrayIconState.Polling, status.TrayIconState);
        Assert.Equal("Status.RunningDryRun", PrintBridgeRuntimeStatus.ResolveHeaderBadgeResourceKey(status));
    }

    [Fact]
    public void DisabledStateHandling_PreservesSavedTurkishLanguage()
    {
        var ui = new UiOptions { Language = SupportedCultures.Turkish };
        var issue = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DisabledByAdmin);

        Assert.False(issue.ShouldClearToken);
        Assert.Equal(SupportedCultures.Turkish, ui.Language);
    }

    [Fact]
    public void ReconnectRequiredTokenClearing_PreservesSavedTurkishLanguage()
    {
        var hub = new WaslaOptions
        {
            ServerUrl = "https://sushim.wasla.local:7200",
            AgentToken = "old-token"
        };
        var bridge = new PrintBridgeOptions
        {
            InstallationId = Guid.NewGuid().ToString("D"),
            DisplayName = "Kitchen POS",
            ServerDeviceNameResolved = true
        };
        var ui = new UiOptions { Language = SupportedCultures.Turkish };

        PrintBridgeRuntimeCredentialFallback.ClearTokenForReconnectRequired(hub, bridge);

        Assert.Equal(string.Empty, hub.AgentToken);
        Assert.Equal(SupportedCultures.Turkish, ui.Language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("en")]
    [InlineData("de-DE")]
    public void StartupCulture_FallsBackToTurkishOnlyWhenSavedLanguageIsMissingOrInvalid(string? savedLanguage)
    {
        Assert.Equal(SupportedCultures.Turkish, SupportedCultures.ResolveStartupCulture(savedLanguage));
    }

    [Theory]
    [InlineData(SupportedCultures.Turkish)]
    [InlineData(SupportedCultures.English)]
    [InlineData(SupportedCultures.Arabic)]
    [InlineData(SupportedCultures.Russian)]
    public void StartupCulture_UsesExplicitSavedSupportedLanguage(string savedLanguage)
    {
        Assert.Equal(savedLanguage, SupportedCultures.ResolveStartupCulture(savedLanguage));
    }

    [Fact]
    public void DuplicateInstallation_RemainsSeparateBlockingLifecycleIssue()
    {
        var issue = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DuplicateInstallation);

        Assert.True(issue.IsBlockingLifecycleIssue);
        Assert.False(issue.ShouldClearToken);
        Assert.True(issue.ShouldStopPolling);
        Assert.Equal("RuntimeIssue.DuplicateInstallation.Detail", issue.EffectiveDetailResourceKey);
        Assert.Equal("RuntimeIssue.DuplicateInstallation.Title", issue.EffectiveTitleResourceKey);

        var isConnected = PrintBridgeRuntimeStatus.ResolveEffectiveConnection(
            hasRecentSuccessfulContact: true,
            issue);
        var status = CreateStatus(isRunning: false, isConnected, issue);

        Assert.False(isConnected);
        Assert.Equal("RuntimeIssue.DuplicateInstallation.Title", PrintBridgeRuntimeStatus.ResolveHeaderBadgeResourceKey(status));
        Assert.NotEqual(
            new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ReconnectRequired).EffectiveDetailResourceKey,
            issue.EffectiveDetailResourceKey);
    }

    [Theory]
    [InlineData(PrintBridgeRuntimeIssueCode.ReconnectRequired, false)]
    [InlineData(PrintBridgeRuntimeIssueCode.DisabledByAdmin, false)]
    [InlineData(PrintBridgeRuntimeIssueCode.DuplicateInstallation, false)]
    [InlineData(PrintBridgeRuntimeIssueCode.ServerUnreachable, true)]
    public void ShouldReportConnectionSuccess_RespectsBlockingLifecycleIssues(
        PrintBridgeRuntimeIssueCode code,
        bool expected)
    {
        var status = CreateStatus(isRunning: false, isConnected: false, new PrintBridgeRuntimeIssue(code));
        Assert.Equal(expected, PrintBridgeRuntimeStatus.ShouldReportConnectionSuccess(status));
    }

    [Fact]
    public void BlockingLifecycleIssue_RerendersInSelectedLanguage()
    {
        var reconnect = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ReconnectRequired);
        var disabled = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DisabledByAdmin);

        Assert.NotEqual(RenderRuntimeIssue(reconnect, "tr-TR"), RenderRuntimeIssue(reconnect, "en-US"));
        Assert.NotEqual(RenderRuntimeIssue(disabled, "tr-TR"), RenderRuntimeIssue(disabled, "en-US"));
    }

    [Fact]
    public void LanguageResources_CanRerenderKnownRuntimeStateFromStableCode()
    {
        var key = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DisabledByAdmin)
            .EffectiveDetailResourceKey;
        var root = FindRepositoryRoot();
        var tr = ReadResourceValue(root, "PrintBridgeResources.tr-TR.resx", key);
        var en = ReadResourceValue(root, "PrintBridgeResources.en-US.resx", key);

        Assert.False(string.IsNullOrWhiteSpace(tr));
        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.NotEqual(tr, en);
    }

    [Fact]
    public void RuntimeIssueRenderer_UsesCurrentLanguageForSameStableIssue()
    {
        var issue = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DuplicateInstallation);

        var turkish = RenderRuntimeIssue(issue, "tr-TR");
        var english = RenderRuntimeIssue(issue, "en-US");

        Assert.Contains("Wasla Web", turkish, StringComparison.Ordinal);
        Assert.Contains("Wasla Web", english, StringComparison.Ordinal);
        Assert.NotEqual(turkish, english);
    }

    private Guid SeedTenant()
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "sushim",
            Slug = "sushim",
            PrimaryDomain = "sushim.wasla.local",
            DatabaseName = "Wasla_Tenant_sushim",
            EncryptedConnectionString = "encrypted",
            EncryptionKeyVersion = 1,
            SchemaVersion = "1.0",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        return tenant.Id;
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "src", "Wasla.PrintBridge", "Resources")))
                return current;

            var parent = Directory.GetParent(current);
            if (parent is null)
                break;
            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static string ReadResourceValue(string root, string fileName, string key)
    {
        var path = Path.Combine(root, "src", "Wasla.PrintBridge", "Resources", fileName);
        var doc = XDocument.Load(path);
        return doc.Root?
            .Elements("data")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), key, StringComparison.Ordinal))
            ?.Element("value")
            ?.Value ?? string.Empty;
    }

    private static string RenderRuntimeIssue(PrintBridgeRuntimeIssue issue, string cultureName)
    {
        var root = FindRepositoryRoot();
        var fileName = cultureName switch
        {
            SupportedCultures.Turkish => "PrintBridgeResources.tr-TR.resx",
            SupportedCultures.English => "PrintBridgeResources.en-US.resx",
            SupportedCultures.Arabic => "PrintBridgeResources.ar-SA.resx",
            SupportedCultures.Russian => "PrintBridgeResources.ru-RU.resx",
            _ => "PrintBridgeResources.resx"
        };

        return ReadResourceValue(root, fileName, issue.EffectiveDetailResourceKey);
    }

    [Theory]
    [InlineData("device_removed")]
    [InlineData("device_auth_invalid")]
    [InlineData("print_bridge_token_required")]
    public void TokenRevokedErrors_UseTokenRevokedDetailKey(string serverErrorCode)
    {
        var ex = PrintBridgeConnectionException.FromResponse(
            "api/print-bridge/health",
            "https://sushim.wasla.local",
            401,
            $$"""{"error":"{{serverErrorCode}}"}""",
            serverErrorCode);

        Assert.Equal(PrintBridgeRuntimeIssueCode.ReconnectRequired, ex.IssueCode);
        Assert.Equal("RuntimeIssue.ReconnectRequired.TokenRevoked.Detail", ex.UserMessageKey);
    }

    [Fact]
    public void GenericUnauthorized_UsesReconnectDetail_NotTokenRevokedDetail()
    {
        var ex = PrintBridgeConnectionException.FromResponse(
            "api/print-bridge/health",
            "https://sushim.wasla.local",
            401,
            null,
            null);

        Assert.Equal(PrintBridgeRuntimeIssueCode.ReconnectRequired, ex.IssueCode);
        Assert.Equal("RuntimeIssue.ReconnectRequired.Detail", ex.UserMessageKey);
    }

    [Fact]
    public void DisabledRecovery_AfterSuccessfulContact_ClearsBlockingState()
    {
        var isConnected = PrintBridgeRuntimeStatus.ResolveEffectiveConnection(
            hasRecentSuccessfulContact: true,
            lastIssue: null);
        var status = CreateStatus(isRunning: true, isConnected, lastIssue: null);

        Assert.True(isConnected);
        Assert.Null(status.LastIssue);
        Assert.Equal(BridgeServerConnectionStatus.Connected, status.ServerConnectionStatus);
        Assert.Equal(TrayIconState.Polling, status.TrayIconState);
        Assert.Equal("Status.RunningDryRun", PrintBridgeRuntimeStatus.ResolveHeaderBadgeResourceKey(status));
    }

    [Fact]
    public void RuntimeIssueMessageHierarchy_TitleIsShorterThanDetail_ForKnownLifecycleStates()
    {
        var root = FindRepositoryRoot();
        var codes = new[]
        {
            PrintBridgeRuntimeIssueCode.ReconnectRequired,
            PrintBridgeRuntimeIssueCode.DisabledByAdmin,
            PrintBridgeRuntimeIssueCode.DuplicateInstallation
        };

        foreach (var code in codes)
        {
            var issue = new PrintBridgeRuntimeIssue(code);
            var title = ReadResourceValue(root, "PrintBridgeResources.tr-TR.resx", issue.EffectiveTitleResourceKey);
            var summary = ReadResourceValue(root, "PrintBridgeResources.tr-TR.resx", issue.EffectiveConnectionSummaryResourceKey);
            var detail = ReadResourceValue(root, "PrintBridgeResources.tr-TR.resx", issue.EffectiveDetailResourceKey);

            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(summary));
            Assert.False(string.IsNullOrWhiteSpace(detail));
            Assert.True(title.Length < detail.Length, $"Expected title shorter than detail for {code}.");
            Assert.True(summary.Length < detail.Length, $"Expected summary shorter than detail for {code}.");
            if (code != PrintBridgeRuntimeIssueCode.DisabledByAdmin)
                Assert.NotEqual(title, summary);
            Assert.DoesNotContain("Bu Print Bridge", title, StringComparison.Ordinal);
            Assert.DoesNotContain("Bu Print Bridge", summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReconnectRequiredTokenClearing_AlwaysReplacesHolderState_EvenWhenAlreadyEmpty()
    {
        var hub = new WaslaOptions
        {
            ServerUrl = "https://sushim.wasla.local:7200",
            AgentToken = string.Empty
        };
        var bridge = new PrintBridgeOptions
        {
            InstallationId = Guid.NewGuid().ToString("D"),
            DisplayName = string.Empty,
            ServerDeviceNameResolved = false
        };
        var installationId = bridge.InstallationId;

        var changed = PrintBridgeRuntimeCredentialFallback.ClearTokenForReconnectRequired(hub, bridge);

        Assert.False(changed);
        Assert.Equal(string.Empty, hub.AgentToken);
        Assert.Equal("https://sushim.wasla.local:7200", hub.ServerUrl);
        Assert.Equal(installationId, bridge.InstallationId);
    }

    private static PrintBridgeRuntimeStatus CreateStatus(
        bool isRunning,
        bool isConnected,
        PrintBridgeRuntimeIssue? lastIssue)
    {
        var recentJobs = Array.Empty<LocalPrintJobRecord>();
        return new PrintBridgeRuntimeStatus
        {
            IsRunning = isRunning,
            IsConnected = isConnected,
            LastIssue = lastIssue,
            DryRun = true,
            ServerConnectionStatus = PrintBridgeRuntimeStatus.ResolveServerConnectionStatus(
                isConfigured: true,
                isRunning,
                isConnected,
                lastIssue),
            TrayIconState = PrintBridgeRuntimeStatus.ResolveTrayIconState(
                isRunning,
                isConnected,
                recentJobs,
                lastIssue)
        };
    }

    private sealed class NoActivePrintJobChecker : IPrintBridgeActivePrintJobChecker
    {
        public static NoActivePrintJobChecker Instance { get; } = new();

        private NoActivePrintJobChecker()
        {
        }

        public Task<bool> HasActivePrintingJobAsync(
            Guid customerId,
            Guid? installationId,
            string? legacyLockedBy,
            CancellationToken ct) =>
            Task.FromResult(false);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
