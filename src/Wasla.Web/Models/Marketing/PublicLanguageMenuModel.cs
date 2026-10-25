namespace Wasla.Web.Models.Marketing;

/// <summary>The language switch of the public header and footer: a disclosure listing the four supported cultures.</summary>
/// <param name="ReturnUrl">The local URL the culture switch returns to.</param>
/// <param name="MenuId">A page-unique id for the list the button controls.</param>
/// <param name="ShowLanguageName">True to show the current language name; otherwise its short code.</param>
public sealed record PublicLanguageMenuModel(string ReturnUrl, string MenuId, bool ShowLanguageName);
