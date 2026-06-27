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

    [Fact]
    public void LanguageResources_CanRerenderKnownRuntimeStateFromStableCode()
    {
        var key = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DisabledByAdmin)
            .EffectiveResourceKey;
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
        var assemblyPath = Path.Combine(
            root,
            "src",
            "Wasla.PrintBridge",
            "bin",
            "Debug",
            "net8.0-windows",
            "Wasla.PrintBridge.dll");
        var resources = new ResourceManager(
            "Wasla.PrintBridge.Resources.PrintBridgeResources",
            Assembly.LoadFrom(assemblyPath));
        var culture = CultureInfo.GetCultureInfo(cultureName);
        return resources.GetString(issue.EffectiveResourceKey, culture) ?? issue.EffectiveResourceKey;
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
