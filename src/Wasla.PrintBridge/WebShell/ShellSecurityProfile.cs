namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// WebView2 settings applied to the status shell. Release builds lock everything down; Debug builds only
/// add DevTools and browser keys (F12, reload) for development. No profile allows host objects, autofill,
/// password saving, context menus, script dialogs, swipe navigation or external drops.
/// </summary>
public sealed record ShellSecurityProfile(
    bool AreDevToolsEnabled,
    bool AreBrowserAcceleratorKeysEnabled,
    bool AreDefaultContextMenusEnabled,
    bool AreHostObjectsAllowed,
    bool IsPasswordAutosaveEnabled,
    bool IsGeneralAutofillEnabled,
    bool IsStatusBarEnabled,
    bool AreDefaultScriptDialogsEnabled,
    bool IsSwipeNavigationEnabled,
    bool AllowExternalDrop,
    bool IsWebMessageEnabled)
{
    public static readonly ShellSecurityProfile Release = new(
        AreDevToolsEnabled: false,
        AreBrowserAcceleratorKeysEnabled: false,
        AreDefaultContextMenusEnabled: false,
        AreHostObjectsAllowed: false,
        IsPasswordAutosaveEnabled: false,
        IsGeneralAutofillEnabled: false,
        IsStatusBarEnabled: false,
        AreDefaultScriptDialogsEnabled: false,
        IsSwipeNavigationEnabled: false,
        AllowExternalDrop: false,
        IsWebMessageEnabled: true);

    public static readonly ShellSecurityProfile Development = Release with
    {
        AreDevToolsEnabled = true,
        AreBrowserAcceleratorKeysEnabled = true
    };

    public static ShellSecurityProfile ForCurrentBuild =>
#if DEBUG
        Development;
#else
        Release;
#endif
}
