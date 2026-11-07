using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// One Wasla backend for the WAS-91 tests: the real setup-session, device and device-authentication services over an
/// in-memory SQLite CentralDb with three fictional restaurants (A, B, C). Each call gets its own DbContext, as each
/// request gets its own scope on the server. Every value is fake. SQLite runs no application locks, so concurrency is
/// covered separately on SQL Server.
/// </summary>
internal sealed class PrintBridgeTransferBackend : IDisposable
{
    public const string HostA = "anadolu.app.wasla.tr";
    public const string HostB = "bahce.app.wasla.tr";
    public const string HostC = "cinar.app.wasla.tr";
    public const string UrlA = "https://anadolu.app.wasla.tr/";
    public const string UrlB = "https://bahce.app.wasla.tr/";
    public const string UrlC = "https://cinar.app.wasla.tr/";
    public const string NameA = "Anadolu Sofrası";
    public const string NameB = "Bahçe Burger";
    public const string NameC = "Çınar Kebap";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CentralDbContext> _options;

    public PrintBridgeTransferBackend()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options;

        using var db = OpenDb();
        db.Database.EnsureCreated();
        TenantA = SeedTenant(db, "anadolu", NameA);
        TenantB = SeedTenant(db, "bahce", NameB);
        TenantC = SeedTenant(db, "cinar", NameC);
    }

    public Guid TenantA { get; }

    public Guid TenantB { get; }

    public Guid TenantC { get; }

    /// <summary>What the active-print-job check answers: a receipt of the source tenant still in Printing (WAS-58/56).</summary>
    public bool PrintingInSourceTenant { get; set; }

    /// <summary>Calls are one at a time, as SQLite has one connection here.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public CentralDbContext OpenDb() => new(_options);

    /// <summary>A context on the same database whose commands pass through <paramref name="interceptors"/>.</summary>
    public CentralDbContext OpenDb(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    public PrintBridgeSetupSessionService SetupService(CentralDbContext db) =>
        new(db, Devices(db), NoLocks.Instance, new Checker(this), NullLogger<PrintBridgeSetupSessionService>.Instance);

    public PrintBridgeDeviceManagementService Devices(CentralDbContext db) =>
        new(db, NullLogger<PrintBridgeDeviceManagementService>.Instance, new Checker(this));

    public PrintBridgeAuthService Auth(CentralDbContext db) => new(db, NullLogger<PrintBridgeAuthService>.Instance);

    /// <summary>A device of <paramref name="tenantId"/> with a fake token, bound to <paramref name="installationId"/>.</summary>
    public async Task<(Guid DeviceId, string Token)> CreateDeviceAsync(Guid tenantId, string name, Guid? installationId)
    {
        await using var db = OpenDb();
        var created = await Devices(db).CreateDeviceAsync(tenantId, name, CancellationToken.None);
        if (installationId.HasValue)
        {
            var device = await db.PrintBridgeDevices.SingleAsync(d => d.Id == created.DeviceId);
            device.InstallationId = installationId;
            await db.SaveChangesAsync();
        }

        return (created.DeviceId, created.RawToken);
    }

    public async Task<string> CreateSessionAsync(
        Guid tenantId,
        string serverUrl,
        PrintBridgeSetupMode mode = PrintBridgeSetupMode.NewDevice,
        Guid? deviceId = null)
    {
        await using var db = OpenDb();
        var created = await SetupService(db).CreateSessionAsync(
            tenantId,
            mode,
            deviceId,
            serverUrl,
            null,
            confirmReplaceActiveToken: true,
            CancellationToken.None);
        return created.Code;
    }

    public async Task<PrintBridgeSetupSession> SessionAsync(string code)
    {
        await using var db = OpenDb();
        var hash = PrintBridgeSetupCode.Hash(code);
        return await db.PrintBridgeSetupSessions.AsNoTracking().SingleAsync(s => s.CodeHash == hash);
    }

    public async Task<PrintBridgeDevice> DeviceAsync(Guid deviceId)
    {
        await using var db = OpenDb();
        return await db.PrintBridgeDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
    }

    public async Task<List<PrintBridgeDevice>> DevicesWithAsync(Guid installationId)
    {
        await using var db = OpenDb();
        return await db.PrintBridgeDevices.AsNoTracking().Where(d => d.InstallationId == installationId).ToListAsync();
    }

    public async Task<int> DeviceCountAsync()
    {
        await using var db = OpenDb();
        return await db.PrintBridgeDevices.CountAsync();
    }

    public async Task ExpireSessionAsync(string code)
    {
        await using var db = OpenDb();
        var hash = PrintBridgeSetupCode.Hash(code);
        var session = await db.PrintBridgeSetupSessions.SingleAsync(s => s.CodeHash == hash);
        session.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>Device authentication as the device middleware runs it, with the installation header.</summary>
    public async Task<PrintBridgeAuthResult> AuthenticateAsync(string token, Guid? installationId)
    {
        await using var db = OpenDb();
        return await Auth(db).AuthenticateDetailedAsync(
            token,
            new PrintBridgeClientInfo("QA-PC", "1.0.0.0", null, "127.0.0.1", installationId),
            CancellationToken.None);
    }

    public void Dispose()
    {
        Gate.Dispose();
        _connection.Dispose();
    }

    private static Guid SeedTenant(CentralDbContext db, string slug, string name)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            PrimaryDomain = $"{slug}.app.wasla.tr",
            DatabaseName = $"Wasla_Tenant_{slug}",
            EncryptedConnectionString = "encrypted-not-real",
            EncryptionKeyVersion = 1,
            SchemaVersion = "1.0",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant.Id;
    }

    private sealed class NoLocks : IPrintBridgeSetupTenantLock
    {
        public static NoLocks Instance { get; } = new();

        public Task<int> AcquireNewDeviceExchangeLockAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(0);

        public Task<int> AcquireInstallationLockAsync(Guid installationId, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class Checker(PrintBridgeTransferBackend backend) : IPrintBridgeActivePrintJobChecker
    {
        public Task<bool> HasActivePrintingJobAsync(Guid tenantId, Guid? installationId, string? legacyLockedBy, CancellationToken ct) =>
            Task.FromResult(backend.PrintingInSourceTenant);
    }
}

/// <summary>
/// The backend's HTTP face for the real Print Bridge client: the setup, move, completion, health and poll routes, answered
/// by the real services with the status codes and error codes of <c>PrintBridgeSetupApiController</c> and the device
/// middleware (their own mapping is covered in Wasla.UnitTests). Every request is recorded with its host, path and
/// token. Faults can be injected: an unreachable host, a request that never reaches the server, and an answer lost after
/// the server processed it.
/// </summary>
internal sealed class PrintBridgeTransferHandler(PrintBridgeTransferBackend backend) : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private int _dropTransfersBeforeServer;
    private int _loseTransferAnswers;

    public ConcurrentQueue<(string Host, string Path, string Token)> Requests { get; } = new();

    /// <summary>Hosts that cannot be reached at all.</summary>
    public HashSet<string> Unreachable { get; } = [];

    /// <summary>How many move requests fail on the way, before the server sees them.</summary>
    public int DropTransfersBeforeServer
    {
        get => Volatile.Read(ref _dropTransfersBeforeServer);
        set => Volatile.Write(ref _dropTransfersBeforeServer, value);
    }

    /// <summary>How many move answers are lost after the server processed (and stored) the move.</summary>
    public int LoseTransferAnswers
    {
        get => Volatile.Read(ref _loseTransferAnswers);
        set => Volatile.Write(ref _loseTransferAnswers, value);
    }

    /// <summary>How long the health check waits before it is answered (a slow server after a move was saved).</summary>
    public TimeSpan HealthDelay { get; set; }

    /// <summary>Every request body, to prove nothing was sent anywhere it must not go.</summary>
    public ConcurrentQueue<(string Host, string Path, string Body)> Bodies { get; } = new();

    public int Count(string path) => Requests.Count(r => r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri!.Host;
        var path = request.RequestUri.AbsolutePath.Trim('/');
        var token = Header(request, "X-PrintBridge-Token") ?? string.Empty;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((host, path, token));
        Bodies.Enqueue((host, path, body));

        if (Unreachable.Contains(host))
            throw new HttpRequestException("No such host is known (fake).");

        if (path == "api/print-bridge/health" && HealthDelay > TimeSpan.Zero)
            await Task.Delay(HealthDelay, ct);

        if (path == "api/print-bridge/setup/transfer" && Interlocked.Decrement(ref _dropTransfersBeforeServer) >= 0)
            throw new HttpRequestException("The connection was reset before the request arrived (fake).");

        await backend.Gate.WaitAsync(ct);
        HttpResponseMessage response;
        try
        {
            await using var db = backend.OpenDb();
            response = await AnswerAsync(db, request, path, token, body, ct);
        }
        finally
        {
            backend.Gate.Release();
        }

        if (path == "api/print-bridge/setup/transfer" && Interlocked.Decrement(ref _loseTransferAnswers) >= 0)
        {
            response.Dispose();
            throw new HttpRequestException("The connection was reset after the server answered (fake).");
        }

        return response;
    }

    private async Task<HttpResponseMessage> AnswerAsync(
        CentralDbContext db,
        HttpRequestMessage request,
        string path,
        string token,
        string body,
        CancellationToken ct)
    {
        var installationId = Guid.TryParse(Header(request, "X-PrintBridge-Installation-Id"), out var parsed) ? parsed : (Guid?)null;
        var setup = backend.SetupService(db);

        switch (path)
        {
            case "api/print-bridge/setup/exchange":
            {
                var exchange = JsonSerializer.Deserialize<ExchangeBody>(body, Json)!;
                try
                {
                    var result = await setup.ExchangeAsync(
                        exchange.Code,
                        new PrintBridgeSetupClientInfo(exchange.MachineName, "1.0.0.0", exchange.PrinterName, exchange.InstallationId, exchange.DeviceName),
                        ct);
                    return result is null
                        ? Answer(HttpStatusCode.BadRequest, new { error = "invalid_or_expired_setup_code" })
                        : Answer(HttpStatusCode.OK, new
                        {
                            sessionId = result.SessionId,
                            serverUrl = result.ServerUrl,
                            deviceToken = result.DeviceToken,
                            deviceName = result.DeviceName,
                            installationId = result.InstallationId,
                            completionCredential = result.CompletionCredential
                        });
                }
                catch (PrintBridgeSetupInstallationAlreadyRegisteredException)
                {
                    return Answer(HttpStatusCode.Conflict, new { error = PrintBridgeSetupFailureReasons.InstallationAlreadyRegistered });
                }
                catch (PrintBridgeSetupInstallationRegisteredToAnotherTenantException)
                {
                    return Answer(HttpStatusCode.Conflict, new { error = PrintBridgeSetupFailureReasons.InstallationRegisteredToAnotherTenant });
                }
            }

            case "api/print-bridge/setup/complete":
            {
                var complete = JsonSerializer.Deserialize<CompleteBody>(body, Json)!;
                var ok = await setup.CompleteAsync(complete.SessionId, complete.CompletionCredential, complete.ConnectionVerified, ct);
                return Answer(HttpStatusCode.OK, new { success = ok });
            }

            case "api/print-bridge/setup/transfer/preview":
            {
                var transfer = JsonSerializer.Deserialize<TransferBody>(body, Json)!;
                var preview = await setup.PreviewTransferAsync(ToRequest(token, installationId, transfer), ct);
                return preview.Outcome switch
                {
                    PrintBridgeTransferOutcome.Ready => Answer(HttpStatusCode.OK, new
                    {
                        sameTenant = false,
                        currentTenantName = preview.CurrentTenantName,
                        targetTenantName = preview.TargetTenantName,
                        targetServerUrl = preview.TargetServerUrl,
                        expiresAtUtc = preview.ExpiresAtUtc
                    }),
                    PrintBridgeTransferOutcome.SameTenant => Answer(HttpStatusCode.OK, new { sameTenant = true }),
                    _ => Refused(preview.Outcome)
                };
            }

            case "api/print-bridge/setup/transfer":
            {
                var transfer = JsonSerializer.Deserialize<TransferBody>(body, Json)!;
                var result = await setup.TransferAsync(ToRequest(token, installationId, transfer), ct);
                return result.Outcome == PrintBridgeTransferOutcome.Ready
                    ? Answer(HttpStatusCode.OK, new
                    {
                        sessionId = result.SessionId,
                        serverUrl = result.ServerUrl,
                        deviceName = result.DeviceName,
                        installationId = result.InstallationId
                    })
                    : Refused(result.Outcome);
            }
        }

        // Device routes: authenticated as the device middleware does.
        var auth = await backend.Auth(db).AuthenticateDetailedAsync(
            token,
            new PrintBridgeClientInfo(Header(request, "X-PrintBridge-Name"), "1.0.0.0", null, "127.0.0.1", installationId),
            ct);
        if (!auth.Succeeded)
        {
            var code = auth.FailureCode switch
            {
                PrintBridgeAuthFailureCode.DeviceRemoved => "device_removed",
                PrintBridgeAuthFailureCode.DeviceDisabled => "device_disabled",
                PrintBridgeAuthFailureCode.InstallationIdMismatch => "installation_mismatch",
                _ => "device_auth_invalid"
            };
            var answer = Answer(auth.FailureCode == PrintBridgeAuthFailureCode.DeviceDisabled ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, new { error = code });
            answer.Headers.TryAddWithoutValidation("X-PrintBridge-Error-Code", code);
            return answer;
        }

        return path switch
        {
            "api/print-bridge/health" => Answer(HttpStatusCode.OK, new
            {
                success = true,
                tenantName = auth.Context!.TenantName,
                deviceName = auth.Context.DeviceName,
                serverTimeUtc = DateTime.UtcNow
            }),
            "api/print-bridge/jobs/pending" => Answer(HttpStatusCode.OK, new { jobs = Array.Empty<object>() }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private static PrintBridgeTransferRequest ToRequest(string token, Guid? installationId, TransferBody body) =>
        new(token, installationId, body.Code, body.ServerUrl, body.DeviceToken, body.CompletionCredential, body.MachineName, "1.0.0.0", body.PrinterName);

    /// <summary>The same status and error codes as <c>PrintBridgeSetupApiController.Refused</c>.</summary>
    private static HttpResponseMessage Refused(PrintBridgeTransferOutcome outcome) =>
        outcome switch
        {
            PrintBridgeTransferOutcome.SourceRejected => Answer(HttpStatusCode.Unauthorized, new { error = "source_credential_rejected" }),
            PrintBridgeTransferOutcome.PrintingInProgress => Answer(HttpStatusCode.Conflict, new { error = "printing_in_progress" }),
            PrintBridgeTransferOutcome.DeviceLimitReached => Answer(HttpStatusCode.Conflict, new { error = "device_limit_reached" }),
            PrintBridgeTransferOutcome.InstallationConflict => Answer(HttpStatusCode.Conflict, new { error = "installation_conflict" }),
            PrintBridgeTransferOutcome.DestinationDeviceDisabled => Answer(HttpStatusCode.Conflict, new { error = "destination_device_disabled" }),
            PrintBridgeTransferOutcome.SameTenant => Answer(HttpStatusCode.Conflict, new { error = "same_tenant" }),
            PrintBridgeTransferOutcome.Failed => Answer(HttpStatusCode.ServiceUnavailable, new { error = "transfer_failed" }),
            _ => Answer(HttpStatusCode.BadRequest, new { error = "invalid_or_expired_setup_code" })
        };

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };

    private sealed record ExchangeBody(string Code, string? MachineName, string? PrinterName, Guid? InstallationId, string? DeviceName);

    private sealed record CompleteBody(Guid SessionId, string CompletionCredential, bool ConnectionVerified);

    private sealed record TransferBody(
        string Code,
        string ServerUrl,
        string? DeviceToken,
        string? CompletionCredential,
        string? MachineName,
        string? PrinterName);
}
