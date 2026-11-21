using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.ReferenceData;

namespace Wasla.Web.Routing;

/// <summary>
/// Whether the pilot application form, its privacy notice and the coming-soon call to action are published (WAS-217).
/// They are open only when all of these hold:
/// <list type="bullet">
/// <item><c>Wasla:PilotApplication:Enabled</c> is true;</item>
/// <item>every required privacy notice fact is configured, or the host runs in the <c>Development</c> environment, where
/// an incomplete notice is shown as a draft with its missing facts marked. Any other environment name (Production,
/// Staging, Pilot, Test or a misspelling) never publishes a draft notice;</item>
/// <item>the CentralDb city catalog holds the 81 Turkish provinces (<see cref="TurkishProvinces.Check"/>), so the form
/// never shows an empty or partial city list.</item>
/// </list>
/// </summary>
/// <remarks>
/// The catalog is read at most once per <see cref="CompleteCatalogLifetime"/> while it is complete and once per
/// <see cref="IncompleteCatalogLifetime"/> while it is not, so an import is picked up within a minute without a query per
/// request. A catalog that turns empty, incomplete or unreadable is logged once, with the reason.
/// </remarks>
public sealed class PilotApplicationGate
{
    public static readonly TimeSpan CompleteCatalogLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan IncompleteCatalogLifetime = TimeSpan.FromMinutes(1);

    private readonly PilotApplicationOptions _options;
    private readonly bool _development;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<PilotApplicationGate> _logger;
    private readonly SemaphoreSlim _catalogRead = new(1, 1);
    private PilotCityCatalogState? _catalog;
    private DateTimeOffset _catalogExpiresAt;

    public PilotApplicationGate(
        IOptions<PilotApplicationOptions> options,
        IWebHostEnvironment environment,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<PilotApplicationGate> logger)
    {
        _options = options.Value;
        _development = environment.IsDevelopment();
        _scopes = scopes;
        _time = time;
        _logger = logger;
        MissingNoticeFields = FindMissingFields(_options.PrivacyNotice);
    }

    /// <summary>The configuration keys of the required notice facts that are empty, in notice order.</summary>
    public IReadOnlyList<string> MissingNoticeFields { get; }

    public bool Enabled => _options.Enabled;

    /// <summary>The notice lacks a required fact; it may be shown only in Development, marked as a draft.</summary>
    public bool NoticeIsDraft => MissingNoticeFields.Count > 0;

    /// <summary>Configuration alone allows the form; the city catalog is checked by <see cref="IsOpenAsync"/>.</summary>
    public bool ConfigurationAllowsForm => _options.Enabled && (!NoticeIsDraft || _development);

    /// <summary>Enabled, but kept closed because the notice lacks required facts outside Development.</summary>
    public bool ClosedForMissingNotice => _options.Enabled && NoticeIsDraft && !_development;

    public PilotPrivacyNoticeOptions Notice => _options.PrivacyNotice;

    /// <summary>Whether the form, its notice and the coming-soon action are published now.</summary>
    public async ValueTask<bool> IsOpenAsync(CancellationToken ct) =>
        ConfigurationAllowsForm && (await GetCityCatalogStateAsync(ct).ConfigureAwait(false)).Status == TurkishProvinceCatalogStatus.Complete;

    /// <summary>The city catalog as last read; read again when the cached state has expired.</summary>
    public async ValueTask<PilotCityCatalogState> GetCityCatalogStateAsync(CancellationToken ct)
    {
        var cached = _catalog;
        if (cached is not null && _time.GetUtcNow() < _catalogExpiresAt)
            return cached;

        await _catalogRead.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalog is not null && _time.GetUtcNow() < _catalogExpiresAt)
                return _catalog;

            var state = await ReadCatalogAsync(ct).ConfigureAwait(false);
            if (state.Status != TurkishProvinceCatalogStatus.Complete && (_catalog is null || _catalog.Status != state.Status || _catalog.Unreadable != state.Unreadable))
            {
                _logger.LogWarning(
                    "The pilot application form stays closed: the CentralDb city catalog is {CatalogStatus} ({ActiveProvinces} of {ExpectedProvinces} Turkish provinces with a plate code{UnreadableReason}). Import it with seed-turkey-reference-data.",
                    state.Unreadable ? "unreadable" : state.Status.ToString().ToLowerInvariant(),
                    state.ActiveProvinces,
                    TurkishProvinces.Count,
                    state.Unreadable ? "; " + state.ExceptionType : string.Empty);
            }
            else if (state.Status == TurkishProvinceCatalogStatus.Complete && _catalog is { Status: not TurkishProvinceCatalogStatus.Complete })
            {
                _logger.LogInformation("The CentralDb city catalog is complete again; the pilot application form follows its configuration.");
            }

            _catalog = state;
            _catalogExpiresAt = _time.GetUtcNow() + (state.Status == TurkishProvinceCatalogStatus.Complete ? CompleteCatalogLifetime : IncompleteCatalogLifetime);
            return state;
        }
        finally
        {
            _catalogRead.Release();
        }
    }

    private async Task<PilotCityCatalogState> ReadCatalogAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var provinces = await scope.ServiceProvider.GetRequiredService<ITurkishProvinceCatalog>().GetActiveProvincesAsync(ct).ConfigureAwait(false);
            return new PilotCityCatalogState(TurkishProvinces.Check(provinces), provinces.Count, Unreadable: false, ExceptionType: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new PilotCityCatalogState(TurkishProvinceCatalogStatus.Empty, 0, Unreadable: true, ExceptionType: ex.GetType().Name);
        }
    }

    private static List<string> FindMissingFields(PilotPrivacyNoticeOptions notice)
    {
        var prefix = $"{PilotApplicationOptions.SectionName}:{nameof(PilotApplicationOptions.PrivacyNotice)}:";
        var required = new (string Name, string? Value)[]
        {
            (nameof(PilotPrivacyNoticeOptions.ControllerName), notice.ControllerName),
            (nameof(PilotPrivacyNoticeOptions.ControllerAddress), notice.ControllerAddress),
            (nameof(PilotPrivacyNoticeOptions.ControllerContact), notice.ControllerContact),
            (nameof(PilotPrivacyNoticeOptions.LegalBasis), notice.LegalBasis),
            (nameof(PilotPrivacyNoticeOptions.Transfers), notice.Transfers),
            (nameof(PilotPrivacyNoticeOptions.Retention), notice.Retention),
            (nameof(PilotPrivacyNoticeOptions.RequestChannel), notice.RequestChannel)
        };

        return required.Where(f => string.IsNullOrWhiteSpace(f.Value)).Select(f => prefix + f.Name).ToList();
    }
}

/// <summary>
/// The city catalog as the pilot gate last read it. <see cref="Unreadable"/> is true when CentralDb could not be read;
/// the form is then closed like an empty catalog.
/// </summary>
public sealed record PilotCityCatalogState(TurkishProvinceCatalogStatus Status, int ActiveProvinces, bool Unreadable, string? ExceptionType);
