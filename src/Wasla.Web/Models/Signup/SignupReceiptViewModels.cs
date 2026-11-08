namespace Wasla.Web.Models.Signup;

/// <summary>
/// Presentation of the signup steps for the signup page and its receipt partial (WAS-194). Built in the view from the
/// page model; it carries no data of its own and is never bound from a request.
/// </summary>
public sealed record SignupFlowStep(string Id, int Number, string Name, string Hint, IReadOnlyList<SignupReceiptLine> Lines);

/// <summary>One value line of a receipt step: its binding key for signup-receipt.js and the server-rendered value.</summary>
public sealed record SignupReceiptLine(string Key, string? Value, bool Ltr = false);

/// <summary>
/// What one copy of the receipt shows; the desktop aside and the mobile sheet differ only in the id prefix.
/// <see cref="AmountLabel"/> says "due today" only where a payment step can follow, otherwise "plan price".
/// </summary>
public sealed record SignupReceiptView(
    string IdPrefix,
    IReadOnlyList<SignupFlowStep> Steps,
    string Current,
    IReadOnlyCollection<string> Completed,
    string AmountLabel,
    string Amount,
    bool AmountSet,
    string? Vat,
    string? PeriodNote,
    string? StripDetail = null)
{
    /// <summary>"current" for the open step, "done" for a completed one, otherwise "upcoming".</summary>
    public string StatusOf(string id) =>
        id == Current ? "current" : Completed.Contains(id) ? "done" : "upcoming";
}
