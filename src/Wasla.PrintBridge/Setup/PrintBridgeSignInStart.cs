namespace Wasla.PrintBridge.Setup;

/// <summary>
/// The app's start at sign-in, which the installer registers for every user (<c>HKLM\...\Run</c>, WAS-62) with
/// <see cref="Argument"/>. When this user's app already runs in this session, such a start ends quietly instead of saying
/// "already running" at sign-in; the per-user, per-session instance lock (WAS-92) still decides.
/// </summary>
public static class PrintBridgeSignInStart
{
    public const string Argument = "--startup";

    public static bool IsRequestedBy(IEnumerable<string>? args) =>
        args?.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase)) == true;
}
