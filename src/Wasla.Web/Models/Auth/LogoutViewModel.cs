namespace Wasla.Web.Models.Auth;

public sealed class LogoutViewModel
{
    /// <summary>
    /// True on the logout failure page: the logout could not be confirmed, so the session may still be open. The page then
    /// links to the logout page to try again instead of posting the logout itself.
    /// </summary>
    public bool Failed { get; init; }
}
