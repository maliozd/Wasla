namespace Wasla.Web.Routing;

/// <summary>
/// The pilot restaurant application (WAS-217), bound from <c>Wasla:PilotApplication</c>. Off by default. Its privacy
/// notice names the data controller and the legal details from configuration only: the code holds no company name,
/// address or legal decision. See <see cref="PilotApplicationGate"/> for when the form is open.
/// </summary>
public sealed class PilotApplicationOptions
{
    public const string SectionName = "Wasla:PilotApplication";

    /// <summary>Whether the pilot form, its privacy notice and the coming-soon call to action are published.</summary>
    public bool Enabled { get; set; }

    public PilotPrivacyNoticeOptions PrivacyNotice { get; set; } = new();
}

/// <summary>
/// The facts of the pilot privacy notice that the company and its legal review must provide (KVKK article 10). Each is
/// shown as configured, in every UI language. While one is empty the notice is a draft, and Production keeps the form
/// closed.
/// </summary>
public sealed class PilotPrivacyNoticeOptions
{
    /// <summary>The data controller's full legal (trade) name.</summary>
    public string? ControllerName { get; set; }

    /// <summary>The data controller's registered address.</summary>
    public string? ControllerAddress { get; set; }

    /// <summary>The data controller's contact address for notice questions, such as an email or KEP address.</summary>
    public string? ControllerContact { get; set; }

    /// <summary>The legal basis of the processing (KVKK article 5) as decided by the legal review.</summary>
    public string? LegalBasis { get; set; }

    /// <summary>Who the data is transferred to (hosting and email providers) and whether any transfer goes abroad.</summary>
    public string? Transfers { get; set; }

    /// <summary>How long an application is kept and how it is deleted or anonymised afterwards.</summary>
    public string? Retention { get; set; }

    /// <summary>Where a data subject sends a request about their rights (KVKK article 11 and 13).</summary>
    public string? RequestChannel { get; set; }

    /// <summary>The date the notice was last updated, <c>yyyy-MM-dd</c>. Optional; shown when set.</summary>
    public string? UpdatedOn { get; set; }
}
