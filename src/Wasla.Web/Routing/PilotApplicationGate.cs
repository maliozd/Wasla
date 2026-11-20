using Microsoft.Extensions.Options;

namespace Wasla.Web.Routing;

/// <summary>
/// Whether the pilot application form and its privacy notice are published (WAS-217). They are open when
/// <c>Wasla:PilotApplication:Enabled</c> is true and either every required privacy notice fact is configured or the host
/// is not Production. Outside Production an incomplete notice is shown as a draft with its missing facts marked, so the
/// flow can be developed and tested; Production never publishes a notice without its controller and legal details.
/// </summary>
public sealed class PilotApplicationGate
{
    private readonly PilotApplicationOptions _options;
    private readonly bool _production;

    public PilotApplicationGate(IOptions<PilotApplicationOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value;
        _production = environment.IsProduction();
        MissingNoticeFields = FindMissingFields(_options.PrivacyNotice);
    }

    /// <summary>The configuration keys of the required notice facts that are empty, in notice order.</summary>
    public IReadOnlyList<string> MissingNoticeFields { get; }

    public bool Enabled => _options.Enabled;

    /// <summary>The notice lacks a required fact; it may be shown only outside Production, marked as a draft.</summary>
    public bool NoticeIsDraft => MissingNoticeFields.Count > 0;

    public bool IsOpen => _options.Enabled && (!NoticeIsDraft || !_production);

    /// <summary>Enabled, but kept closed because Production lacks required notice facts.</summary>
    public bool ClosedForMissingNotice => _options.Enabled && NoticeIsDraft && _production;

    public PilotPrivacyNoticeOptions Notice => _options.PrivacyNotice;

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
