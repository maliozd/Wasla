using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Signup;

public enum RegistrationStatusTone
{
    Neutral,
    Success,
    Danger
}

public enum RegistrationStepState
{
    Done,
    Current,
    Upcoming,
    Failed
}

/// <summary>One line of the status page's progress list: resource keys for its name and description.</summary>
public sealed record RegistrationStatusStep(string NameKey, string DescriptionKey, RegistrationStepState State);

/// <summary>The status chip of the registration card.</summary>
public sealed record RegistrationStatusBadge(string LabelKey, RegistrationStatusTone Tone);

/// <summary>
/// What the registration status and checkout pages say for a registration status (Figma "03 · Güncel kayıt durumu ve
/// ödeme"). Every state comes from the stored <see cref="PendingRegistrationStatus"/>: the pages never infer a payment,
/// a preparation step or a ready panel from anything else. Where only the Development payment simulator can have
/// produced a state, <c>simulator</c> selects the copy that says so; elsewhere the copy says the payment was not
/// completed and never mentions a simulation.
/// </summary>
public static class RegistrationStatusPresentation
{
    public static RegistrationStatusBadge Badge(PendingRegistrationStatus status, bool simulator) => status switch
    {
        PendingRegistrationStatus.PaymentSucceeded => new("Signup.PaymentStatusPaid", RegistrationStatusTone.Success),
        PendingRegistrationStatus.Provisioned => new("RegistrationStatus.PanelReady", RegistrationStatusTone.Success),
        PendingRegistrationStatus.PaymentFailed => new(PaymentNotCompletedKey(simulator), RegistrationStatusTone.Danger),
        PendingRegistrationStatus.Cancelled => new("Signup.PaymentStatusCancelled", RegistrationStatusTone.Neutral),
        PendingRegistrationStatus.Expired => new("Signup.PaymentStatusExpired", RegistrationStatusTone.Neutral),
        _ => new("Signup.PaymentStatusPending", RegistrationStatusTone.Neutral)
    };

    /// <summary>
    /// The progress list. A ready panel has none (its sign-in card replaces it); a cancelled or expired registration
    /// has no further step.
    /// </summary>
    public static IReadOnlyList<RegistrationStatusStep> Steps(PendingRegistrationStatus status, bool simulator) => status switch
    {
        PendingRegistrationStatus.Draft or PendingRegistrationStatus.AwaitingPayment =>
        [
            new("RegistrationStatus.StepRegistered", "RegistrationStatus.StepRegisteredBody", RegistrationStepState.Done),
            new("Signup.PaymentStatusPending", "RegistrationStatus.StepAwaitingPaymentBody", RegistrationStepState.Current),
            new("RegistrationStatus.StepPanelSetup", "RegistrationStatus.StepPanelSetupBody", RegistrationStepState.Upcoming)
        ],
        PendingRegistrationStatus.PaymentSucceeded =>
        [
            new("Signup.PaymentStatusPaid", "RegistrationStatus.StepPaymentReceivedBody", RegistrationStepState.Done),
            new("RegistrationStatus.StepPanelWaiting", "RegistrationStatus.StepPanelWaitingBody", RegistrationStepState.Current),
            new("RegistrationStatus.PanelReady", "RegistrationStatus.StepPanelReadyBody", RegistrationStepState.Upcoming)
        ],
        PendingRegistrationStatus.PaymentFailed =>
        [
            new("RegistrationStatus.StepRegistered", "RegistrationStatus.StepRegisteredBody", RegistrationStepState.Done),
            new(PaymentNotCompletedKey(simulator), "RegistrationStatus.StepPaymentNotCompletedBody", RegistrationStepState.Failed),
            new("RegistrationStatus.StepPanelSetup", "RegistrationStatus.StepPanelSetupBody", RegistrationStepState.Upcoming)
        ],
        _ => []
    };

    /// <summary>The page heading of the status page. A browser without ownership proof gets the neutral one.</summary>
    public static string TitleKey(PendingRegistrationStatus status, bool simulator, bool isOwner)
    {
        if (!isOwner)
            return "RegistrationStatus.PublicTitle";

        return status switch
        {
            PendingRegistrationStatus.PaymentSucceeded => "Signup.PaymentReceivedPreparingTitle",
            PendingRegistrationStatus.Provisioned => "Signup.PanelReadyTitle",
            PendingRegistrationStatus.PaymentFailed => simulator ? "Checkout.FailedTitle" : "Checkout.PaymentNotCompletedTitle",
            PendingRegistrationStatus.Cancelled => "Checkout.CancelledTitle",
            PendingRegistrationStatus.Expired => "RegistrationStatus.ExpiredTitle",
            _ => "Signup.PendingTitle"
        };
    }

    public static string LeadKey(PendingRegistrationStatus status, bool simulator, bool isOwner)
    {
        if (!isOwner)
            return "RegistrationStatus.PublicLead";

        return status switch
        {
            PendingRegistrationStatus.PaymentSucceeded => "RegistrationStatus.PaymentReceivedLead",
            PendingRegistrationStatus.Provisioned => "Signup.PanelReadyBody",
            PendingRegistrationStatus.PaymentFailed => simulator ? "Checkout.FailedNoWorkspace" : "Checkout.PaymentNotCompletedMessage",
            PendingRegistrationStatus.Cancelled => "Checkout.CancelledMessage",
            PendingRegistrationStatus.Expired => "Checkout.StatusExpired",
            _ => simulator ? "Signup.PendingSubtitle" : "Signup.PendingSubtitleAwaitingPayment"
        };
    }

    /// <summary>
    /// True only for an absolute http(s) address: the panel action is shown when the server built a sign-in address
    /// for a provisioned registration, never for an empty or malformed value.
    /// </summary>
    public static bool IsUsablePanelLoginUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string PaymentNotCompletedKey(bool simulator) =>
        simulator ? "Signup.PaymentStatusFailed" : "Checkout.PaymentNotCompletedTitle";
}
