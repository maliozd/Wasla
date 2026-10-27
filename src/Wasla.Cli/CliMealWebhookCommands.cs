using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.Cli;

/// <summary>
/// Operator commands of the Trendyol GO Meal webhook stage pilot. They read the same <c>TrendyolGoMealWebhookPilot</c>
/// and <c>Platform:TrendyolGo</c> configuration as Api and Worker, and run only against exactly the stage API. Seller
/// credentials come from the pilot's encrypted platform connection and are never printed; the integrator token lives
/// only in the operator's token file; the header secret is generated once and only its SHA-256 is printed. The
/// integrator is registered with the configured <c>ReceiverBaseUrl</c> only, and is enabled or asked for a test order
/// only while Get Integrator reports exactly the expected registration (<see cref="TrendyolGoMealIntegratorExpectation"/>).
/// </summary>
internal static class CliMealWebhookCommands
{
    private static readonly Regex IntegratorName = new("^[A-Za-z0-9]{3,64}$", RegexOptions.CultureInvariant);

    public static async Task<int> IntegratorAsync(
        IHost host,
        string action,
        string name,
        string tokenFile,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name) || !IntegratorName.IsMatch(name))
            return Fail("--name must be 3 to 64 letters or digits (Trendyol GO requires an alphanumeric, unique name).");

        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var pilot = configuration.GetSection(TrendyolGoMealWebhookPilotOptions.SectionName).Get<TrendyolGoMealWebhookPilotOptions>()
                    ?? new TrendyolGoMealWebhookPilotOptions();
        if (pilot.TenantId == Guid.Empty || pilot.PlatformConnectionId == Guid.Empty
            || string.IsNullOrWhiteSpace(pilot.SellerId) || string.IsNullOrWhiteSpace(pilot.StoreId)
            || string.IsNullOrWhiteSpace(pilot.HeaderName))
        {
            return Fail("TrendyolGoMealWebhookPilot needs TenantId, PlatformConnectionId, SellerId, StoreId and HeaderName.");
        }

        var receiverBaseUrl = TrendyolGoMealIntegratorExpectation.CanonicalReceiverBaseUrl(pilot.ReceiverBaseUrl);
        if (receiverBaseUrl is null)
        {
            return Fail($"TrendyolGoMealWebhookPilot:ReceiverBaseUrl must be exactly https://<host>{TrendyolGoMealIntegratorExpectation.ReceiverPath}, " +
                        "without user info, port, query or fragment, and shorter than 100 characters.");
        }

        var trendyolGo = configuration.GetSection(TrendyolGoOptions.SectionName).Get<TrendyolGoOptions>() ?? new TrendyolGoOptions();
        if (!TrendyolGoMealIntegratorExpectation.IsStageApiBaseUrl(trendyolGo.BaseUrl))
            return Fail($"Platform:TrendyolGo:BaseUrl must be exactly {TrendyolGoMealIntegratorExpectation.StageApiBaseUrl} for the stage pilot.");
        var apiBase = new Uri(TrendyolGoMealIntegratorExpectation.StageApiBaseUrl + "/");

        var tenant = await CliCommands.ResolveTenantAsync(host, slug: null, pilot.TenantId.ToString(), ct).ConfigureAwait(false);
        if (tenant is null)
            return Fail("The pilot's tenant is not an active tenant in CentralDb.");

        string apiKey, apiSecret, executor;
        await using (var db = await CliCommands.OpenTenantDbAsync(host, tenant, ct).ConfigureAwait(false))
        {
            var connection = await db.PlatformConnections.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == pilot.PlatformConnectionId, ct)
                .ConfigureAwait(false);
            if (!pilot.IsPilotConnection(connection))
            {
                return Fail("The pilot's platform connection is missing, inactive, or not the configured Trendyol GO seller and store.");
            }

            if (string.IsNullOrWhiteSpace(connection!.ExecutorEmail))
                return Fail("The pilot's platform connection has no executor e-mail (x-executor-user).");

            var secrets = host.Services.GetRequiredService<ISecretManager>();
            apiKey = await secrets.DecryptAsync(connection.EncryptedApiKey, connection.EncryptionKeyVersion, ct).ConfigureAwait(false);
            apiSecret = await secrets.DecryptAsync(connection.EncryptedApiSecret, connection.EncryptionKeyVersion, ct).ConfigureAwait(false);
            executor = connection.ExecutorEmail;
        }

        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = apiBase,
            Timeout = TimeSpan.FromSeconds(30)
        };
        var client = new TrendyolGoMealIntegratorClient(http, trendyolGo.AgentName, executor, pilot.SellerId, apiKey, apiSecret);

        try
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "create":
                    return await CreateAsync(client, pilot, name, receiverBaseUrl, tokenFile, ct).ConfigureAwait(false);
                case "show":
                {
                    var check = await TrendyolGoMealIntegratorOperations.CheckAsync(client, name, ReadToken(tokenFile), receiverBaseUrl, pilot, ct).ConfigureAwait(false);
                    Print(name, check);
                    return check.Matches ? 0 : 3;
                }
                case "enable":
                {
                    var (result, check) = await TrendyolGoMealIntegratorOperations.EnableAsync(client, name, ReadToken(tokenFile), receiverBaseUrl, pilot, ct).ConfigureAwait(false);
                    Print(name, check);
                    return result switch
                    {
                        TrendyolGoMealIntegratorEnableResult.Enabled => Done("Integrator enabled."),
                        TrendyolGoMealIntegratorEnableResult.RefusedBeforeEnabling => Fail("Not enabled: the integrator's registration is not exactly the expected one."),
                        _ => Fail("The integrator did not read back as enabled with the expected registration; it was disabled again.")
                    };
                }
                case "disable":
                    await client.DisableAsync(name, ReadToken(tokenFile), ct).ConfigureAwait(false);
                    Console.WriteLine("Integrator disabled.");
                    return 0;
                case "test-order":
                {
                    var (orderNumber, check, requested) = await TrendyolGoMealIntegratorOperations.RequestTestOrderAsync(client, name, ReadToken(tokenFile), receiverBaseUrl, pilot, ct).ConfigureAwait(false);
                    Print(name, check);
                    return requested
                        ? Done($"Webhook test order requested. OrderNumber={orderNumber ?? "-"}")
                        : Fail("No test order requested: the integrator must be enabled with exactly the expected registration.");
                }
                default:
                    return Fail("--action must be create, show, enable, disable or test-order.");
            }
        }
        catch (TrendyolGoMealIntegratorException ex)
        {
            return Fail(ex.Message);
        }
        catch (TokenFileException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static async Task<int> CreateAsync(
        TrendyolGoMealIntegratorClient client,
        TrendyolGoMealWebhookPilotOptions pilot,
        string name,
        string receiverBaseUrl,
        string tokenFile,
        CancellationToken ct)
    {
        if (File.Exists(tokenFile))
            return Fail("--token-file already exists; the integrator token is never overwritten.");

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = await client.CreateAsync(
                name,
                new Uri(receiverBaseUrl),
                TrendyolGoMealIntegratorExpectation.WebhookTimeoutMs,
                TrendyolGoMealWebhookEvents.Documented,
                pilot.HeaderName,
                secret,
                ct)
            .ConfigureAwait(false);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var file = new FileStream(tokenFile, options))
        await using (var writer = new StreamWriter(file))
            await writer.WriteAsync(token).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        Console.WriteLine($"Integrator created for {receiverBaseUrl}; Trendyol GO creates it disabled.");
        Console.WriteLine($"Token written to {tokenFile} (owner read/write only). It is not shown here.");
        Console.WriteLine("Set this on the Api and Worker, then restart them before enabling:");
        Console.WriteLine($"  TrendyolGoMealWebhookPilot__SecretSha256={hash}");
        Console.WriteLine("The header secret itself is not shown or stored by Wasla; only the integrator holds it.");
        return 0;
    }

    /// <summary>Prints Get Integrator's view and, when it is not exactly the expected registration, every difference.</summary>
    private static void Print(string name, TrendyolGoMealIntegratorCheck check)
    {
        var view = check.View;
        Console.WriteLine($"Integrator {name}");
        Console.WriteLine($"  Enabled:      {view.Enabled}");
        Console.WriteLine($"  Base URL:     {view.BaseUrl ?? "-"}");
        Console.WriteLine($"  Timeout (ms): {view.TimeoutMs?.ToString() ?? "-"}");
        Console.WriteLine($"  Modified:     {ProviderTime(view.LastModifiedDate)}");
        Console.WriteLine($"  Seller ids:   {(view.SellerIds.Count == 0 ? "-" : string.Join(", ", view.SellerIds))}");
        foreach (var (eventType, destination) in view.EventDestinations.OrderBy(e => e.Key, StringComparer.Ordinal))
            Console.WriteLine($"  Event {eventType,-20} -> {destination ?? "-"}");

        if (check.Matches)
            return;

        Console.Error.WriteLine("The integrator's registration is not exactly the expected one; the pilot must not be used with it:");
        foreach (var difference in check.Differences)
            Console.Error.WriteLine("  - " + difference);
    }

    /// <summary>A provider's Unix time in milliseconds for display; "-" when missing or outside what a date can hold.</summary>
    private static string ProviderTime(long? milliseconds) =>
        milliseconds is { } ms and >= 0 && ms <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("u", System.Globalization.CultureInfo.InvariantCulture)
            : "-";

    public static async Task<int> InboxAsync(IHost host, int limit, string? replay, CancellationToken ct)
    {
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var pilot = configuration.GetSection(TrendyolGoMealWebhookPilotOptions.SectionName).Get<TrendyolGoMealWebhookPilotOptions>();
        if (pilot is null || pilot.TenantId == Guid.Empty)
            return Fail("TrendyolGoMealWebhookPilot:TenantId is not configured.");

        var tenant = await CliCommands.ResolveTenantAsync(host, slug: null, pilot.TenantId.ToString(), ct).ConfigureAwait(false);
        if (tenant is null)
            return Fail("The pilot's tenant is not an active tenant in CentralDb.");

        await using var db = await CliCommands.OpenTenantDbAsync(host, tenant, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(replay))
        {
            if (!Guid.TryParse(replay, out var eventId))
                return Fail("--replay must be an event id (GUID).");
            var requeued = await TrendyolGoMealWebhookProcessor.ReplayAsync(db, eventId, DateTime.UtcNow, ct).ConfigureAwait(false);
            Console.WriteLine(requeued
                ? $"Event {eventId} is queued again; the Worker processes it within seconds."
                : $"Event {eventId} was not replayed: only Failed or Processed events can be.");
            if (!requeued)
                return 2;
        }

        var events = await TrendyolGoMealWebhookInspection.ListAsync(db, limit, ct).ConfigureAwait(false);
        if (events.Count == 0)
        {
            Console.WriteLine("No Trendyol GO Meal webhook events have been received.");
            return 0;
        }

        foreach (var e in events)
        {
            Console.WriteLine(
                $"{e.Id}  Received={e.ReceivedAt:u}  Event={e.EventType}  Package={e.ExternalOrderId}  Deliveries={e.DeliveryCount}  " +
                $"Status={e.Status}  Attempts={e.Attempts}  Outcome={e.Outcome ?? "-"}  Order={e.OrderId?.ToString() ?? "-"}  " +
                $"OrderCode={e.OrderCode ?? "-"}  OrderStatus={e.OrderStatus?.ToString() ?? "-"}  " +
                $"Processed={e.ProcessedAt?.ToString("u") ?? "-"}  Next={(e.Status == ProviderWebhookEventStatus.Pending ? e.NextAttemptAt.ToString("u") : "-")}  " +
                $"Error={e.LastError ?? "-"}");
        }

        return 0;
    }

    private static string ReadToken(string tokenFile)
    {
        if (!File.Exists(tokenFile))
            throw new TokenFileException("--token-file does not exist; run --action create first.");
        var token = File.ReadAllText(tokenFile).Trim();
        return token.Length == 0 ? throw new TokenFileException("--token-file is empty.") : token;
    }

    private static int Done(string message)
    {
        Console.WriteLine(message);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private sealed class TokenFileException(string message) : Exception(message);
}
