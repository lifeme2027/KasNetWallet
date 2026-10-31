namespace KasNetWallet.Core;

/// <summary>
/// G1: the pre-rename data folder (%APPDATA%/KaspaDesktopWallet). Detects wallets left there, imports them into the new
/// folder without overwriting, and deletes the old copy only after explicit user confirmation.
/// </summary>
public static class LegacyData
{
    public sealed record Status(string LegacyDir, string NewDir, bool MigratedThisRun, IReadOnlyList<string> LegacyWallets, IReadOnlyList<string> ImportableWallets)
    {
        /// <summary>Old folder still holds encrypted wallet file(s).</summary>
        public bool LegacyHasWallets => LegacyWallets.Count > 0;
        /// <summary>Both folders exist and the old one has wallets the new one lacks (they would otherwise be invisible).</summary>
        public bool HasImportable => ImportableWallets.Count > 0;
    }

    private static IEnumerable<string> WalletFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "wallet.*.json").Select(Path.GetFileName).OfType<string>().OrderBy(x => x, StringComparer.Ordinal) : Enumerable.Empty<string>();

    public static Status Inspect(string legacyDir, string newDir, bool migratedThisRun)
    {
        var legacy = WalletFiles(legacyDir).ToArray();
        var importable = legacy.Where(f => !File.Exists(Path.Combine(newDir, f))).ToArray();
        return new Status(legacyDir, newDir, migratedThisRun, legacy, importable);
    }

    /// <summary>Copies the importable wallet files (plus their history/unlock-state) into the new folder. Never overwrites.</summary>
    public static IReadOnlyList<string> Import(Status st)
    {
        Directory.CreateDirectory(st.NewDir);
        var done = new List<string>();
        foreach (var w in st.ImportableWallets)
        {
            var net = w["wallet.".Length..^".json".Length];
            foreach (var f in new[] { w, $"history.{net}.json", $"unlock-state.{net}.json" })
            {
                var src = Path.Combine(st.LegacyDir, f); var dst = Path.Combine(st.NewDir, f);
                if (File.Exists(src) && !File.Exists(dst)) WalletSettings.CopyWritable(src, dst);
            }
            done.Add(w);
        }
        WalletLog.Warn($"Imported {done.Count} wallet file(s) from the legacy data folder.");
        return done;
    }

    public static string NoticeText(Status st) => st.MigratedThisRun
        ? $"Your wallet data was copied from the old folder\n{st.LegacyDir}\nto\n{st.NewDir}.\n\n" +
          "The old folder still contains an ENCRYPTED COPY of your wallet. If you change your password, the old copy keeps the OLD password; " +
          "if that password ever leaks, the old copy is a risk.\n\nDelete the old copy now? (Make sure you have your recovery phrase backed up.)"
        : $"The old folder\n{st.LegacyDir}\ncontains wallet file(s) that this version does not show: {string.Join(", ", st.ImportableWallets)}.\n\n" +
          "Import them into the new folder now? Nothing is overwritten.";

    /// <summary>Deletes the legacy folder. Requires <paramref name="userConfirmed"/>; refuses if the new folder lacks any legacy wallet.</summary>
    public static void DeleteLegacy(Status st, bool userConfirmed)
    {
        if (!userConfirmed) throw new InvalidOperationException("Deleting the old wallet copy requires explicit user confirmation.");
        var missing = WalletFiles(st.LegacyDir).Where(f => !File.Exists(Path.Combine(st.NewDir, f))).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("The new folder does not have every wallet from the old folder yet; import first.");
        if (!Directory.Exists(st.LegacyDir)) return;
        foreach (var f in Directory.GetFiles(st.LegacyDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(st.LegacyDir, recursive: true);
        WalletLog.Warn("Legacy data folder deleted at the user's request.");
    }
}
