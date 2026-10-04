namespace Wasla.PrintBridge.UI;

internal static class PrintBridgeConnectionFieldSync
{
    public static bool ShouldPreserveAgentTokenInput(
        bool isSavingConnection,
        bool isTokenFieldFocused,
        bool userEditedAgentToken) =>
        isSavingConnection || isTokenFieldFocused || userEditedAgentToken;

    public static bool ShouldTreatAgentTokenTextChangeAsUserEdit(
        bool isSyncingConnectionFields,
        bool isTokenFieldFocused) =>
        !isSyncingConnectionFields && isTokenFieldFocused;

    public static bool ShouldClearConnectionStatusOnFieldTextChange(
        bool isSyncingConnectionFields,
        bool isFieldFocused) =>
        !isSyncingConnectionFields && isFieldFocused;
}
