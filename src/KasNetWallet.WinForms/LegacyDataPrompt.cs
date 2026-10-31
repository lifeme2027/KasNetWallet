using KasNetWallet.Core;

namespace KasNetWallet.WinForms;

/// <summary>G1/S5/S6: start-up notice about the pre-rename data folder.</summary>
internal static class LegacyDataPrompt
{
    public static void Run(string newDir)
    {
        try
        {
            var st = LegacyData.Inspect(WalletSettings.LegacyDirectory, newDir, WalletSettings.LastMigratedFrom is not null);
            if (!st.LegacyHasWallets || st.SameFolder) return;
            if (st.HasImportable)
            {
                if (MessageBox.Show(LegacyData.NoticeText(st), "Old wallet folder found", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                var hint = LegacyData.ConflictHint(LegacyData.Import(st));
                if (hint.Length > 0) MessageBox.Show(hint, "Imported under a new name", MessageBoxButtons.OK, MessageBoxIcon.Information);
                st = LegacyData.Inspect(st.LegacyDir, newDir, migratedThisRun: true);
            }
            if (!st.ShouldOfferDelete) return;
            if (MessageBox.Show(LegacyData.NoticeText(st), "Old wallet copy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes
                || MessageBox.Show($"Permanently delete the verified wallet files in\n{st.LegacyDir}\n(including the encrypted wallet copy)? This cannot be undone." + LegacyData.KeptText(LegacyData.PlanDelete(st).Keep),
                    "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            { LegacyData.RecordDeclined(st); return; }
            var done = LegacyData.DeleteLegacy(st, userConfirmed: true);
            MessageBox.Show(done.Keep.Count == 0 ? "The old folder was deleted." : $"Deleted {done.Delete.Count} verified file(s)." + LegacyData.KeptText(done.Keep), "Old wallet copy", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorReporter.Show(null, "Old wallet folder", ex);
        }
    }
}
