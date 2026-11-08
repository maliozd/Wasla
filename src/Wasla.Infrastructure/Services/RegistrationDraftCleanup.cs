using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Deletes what registration drafts leave behind (WAS-192): expired drafts with their verification rows, and send records
/// older than the longest send window. Reads already treat an expired draft as missing, so nothing waits for this.
/// </summary>
/// <remarks>Set-based and idempotent, so several Web instances may run it. Logs counts only.</remarks>
public sealed class RegistrationDraftCleanup
{
    public const int BatchSize = 500;

    /// <summary>The longest rolling send window (the per-day limits).</summary>
    public static readonly TimeSpan SendRetention = TimeSpan.FromDays(1);

    private readonly CentralDbContext _central;
    private readonly TimeProvider _time;
    private readonly ILogger<RegistrationDraftCleanup> _logger;

    public RegistrationDraftCleanup(CentralDbContext central, TimeProvider time, ILogger<RegistrationDraftCleanup> logger)
    {
        _central = central;
        _time = time;
        _logger = logger;
    }

    public sealed record Result(int Drafts, int Verifications, int Sends);

    public async Task<Result> RunOnceAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        int drafts = 0, verifications = 0, sends = 0;

        while (true)
        {
            var expired = await _central.RegistrationDrafts.AsNoTracking()
                .Where(d => d.ExpiresAtUtc <= now)
                .OrderBy(d => d.ExpiresAtUtc)
                .Select(d => d.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (expired.Count == 0)
                break;

            // Explicitly first, so the result does not depend on the provider enforcing the cascade.
            verifications += await _central.RegistrationPhoneVerifications.Where(r => expired.Contains(r.DraftId)).ExecuteDeleteAsync(ct);
            drafts += await _central.RegistrationDrafts.Where(d => expired.Contains(d.Id) && d.ExpiresAtUtc <= now).ExecuteDeleteAsync(ct);
            if (expired.Count < BatchSize)
                break;
        }

        var sentBefore = now - SendRetention;
        while (true)
        {
            var old = await _central.RegistrationPhoneVerificationSends.AsNoTracking()
                .Where(s => s.SentAtUtc < sentBefore)
                .OrderBy(s => s.SentAtUtc)
                .Select(s => s.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (old.Count == 0)
                break;

            sends += await _central.RegistrationPhoneVerificationSends.Where(s => old.Contains(s.Id)).ExecuteDeleteAsync(ct);
            if (old.Count < BatchSize)
                break;
        }

        if (drafts + verifications + sends > 0)
        {
            _logger.LogInformation(
                "Registration draft cleanup deleted rows. Drafts={Drafts} Verifications={Verifications} Sends={Sends}",
                drafts,
                verifications,
                sends);
        }

        return new Result(drafts, verifications, sends);
    }
}

/// <summary>Runs <see cref="RegistrationDraftCleanup"/> every <c>Wasla:Signup:Draft:CleanupInterval</c>, the first time one interval after start.</summary>
public sealed class RegistrationDraftCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SignupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RegistrationDraftCleanupService> _logger;

    public RegistrationDraftCleanupService(
        IServiceScopeFactory scopes,
        IOptions<SignupOptions> options,
        TimeProvider time,
        ILogger<RegistrationDraftCleanupService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Draft.CleanupInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RegistrationDraftCleanup>().RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The next tick tries again; a failed run deletes nothing it should keep.
                    _logger.LogWarning("Registration draft cleanup failed. ExceptionType={ExceptionType}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
