using KasNetWallet.Core.Transfer;

namespace KasNetWallet.WinForms;

/// <summary>D-btn: the default (Enter) button of the change-blocked dialog is always Cancel, never "add the remainder".</summary>
internal static class ChangeBlockedDialog
{
    public static MessageBoxDefaultButton DefaultButton(bool hasSuggestion) =>
        ChangeBlockedException.CancelButtonNumber(hasSuggestion) == 3 ? MessageBoxDefaultButton.Button3 : MessageBoxDefaultButton.Button2;
}
