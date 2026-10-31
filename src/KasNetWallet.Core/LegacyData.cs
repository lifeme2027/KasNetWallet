using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KasNetWallet.Core;

/// <summary>
/// G1/S5/S6/S7: the pre-rename data folder (%APPDATA%/KaspaDesktopWallet). Wallets are compared by CONTENT (SHA-256), never by
/// file name. Import never overwrites (a same-name, different-content wallet is imported under a new name). The old folder is
/// deleted only after explicit confirmation, and only if every old wallet has a byte-identical copy in the new folder.
/// </summary>
public static class LegacyData
{
    public const string ReminderFile = "legacy-reminder.json";
    /// <summary>After this many declined delete offers the app stops asking (first offer + one reminder).</summary>
    public const int MaxDeleteOffers = 2;

    public sealed record Status(string LegacyDir, string NewDir, bool MigratedThisRun, IReadOnlyList<string> LegacyWallets,
        IReadOnlyList<string> ImportableWallets, IReadOnlyList<string> Conflicts, int DeclinedDeletes)
    {
        public bool LegacyHasWallets => LegacyWallets.Count > 0;
        /// <summary>Old wallets with no byte-identical copy in the new folder (they are invisible to the user).</summary>
        public bool HasImportable => ImportableWallets.Count > 0;
        public bool SameFolder => SamePath(LegacyDir, NewDir);
        /// <summary>Offer deleting the old copy: first time and one reminder on a later start.</summary>
        public bool ShouldOfferDelete => LegacyHasWallets && !HasImportable && !SameFolder && (MigratedThisRun || DeclinedDeletes < MaxDeleteOffers);
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string[] WalletFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "wallet.*.json").Select(Path.GetFileName).OfType<string>().OrderBy(x => x, StringComparer.Ordinal).ToArray() : Array.Empty<string>();

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static HashSet<string> NewHashes(string newDir) => WalletFiles(newDir).Select(f => Hash(Path.Combine(newDir, f))).ToHashSet();

    public static Status Inspect(string legacyDir, string newDir, bool migratedThisRun)
    {
        var legacy = WalletFiles(legacyDir);
        var have = NewHashes(newDir);
        var importable = legacy.Where(f => !have.Contains(Hash(Path.Combine(legacyDir, f)))).ToArray();
        var conflicts = importable.Where(f => File.Exists(Path.Combine(newDir, f))).ToArray();
        return new Status(legacyDir, newDir, migratedThisRun, legacy, importable, conflicts, ReadDeclines(newDir));
    }

    /// <summary>Name the old wallet gets in the new folder: same name if free, else wallet.&lt;net&gt;.legacy-&lt;hash8&gt;.json.</summary>
    public static string ImportName(Status st, string legacyFile)
    {
        if (!File.Exists(Path.Combine(st.NewDir, legacyFile))) return legacyFile;
        var net = legacyFile["wallet.".Length..^".json".Length];
        return $"wallet.{net}.legacy-{Hash(Path.Combine(st.LegacyDir, legacyFile))[..8]}.json";
    }

    /// <summary>Copies importable wallets (+ history/unlock-state when the wallet keeps its name) and merges settings. Never overwrites.
    /// Returns legacy name → new name.</summary>
    public static IReadOnlyDictionary<string, string> Import(Status st)
    {
        if (st.SameFolder) throw new InvalidOperationException("Old and new data folders are the same folder.");
        Directory.CreateDirectory(st.NewDir);
        var done = new Dictionary<string, string>();
        foreach (var w in st.ImportableWallets)
        {
            var target = ImportName(st, w);
            if (File.Exists(Path.Combine(st.NewDir, target))) throw new IOException($"Refusing to overwrite {target}.");
            WalletSettings.CopyWritable(Path.Combine(st.LegacyDir, w), Path.Combine(st.NewDir, target));
            if (target == w)
            {
                var net = w["wallet.".Length..^".json".Length];
                foreach (var f in new[] { $"history.{net}.json", $"unlock-state.{net}.json" })
                {
                    var src = Path.Combine(st.LegacyDir, f); var dst = Path.Combine(st.NewDir, f);
                    if (File.Exists(src) && !File.Exists(dst)) WalletSettings.CopyWritable(src, dst);
                }
            }
            done[w] = target;
        }
        MergeSettings(st.LegacyDir, st.NewDir);
        WalletLog.Warn($"Imported {done.Count} wallet file(s) from the legacy data folder ({done.Count(kv => kv.Key != kv.Value)} renamed).");
        return done;
    }

    /// <summary>Old settings.json: copied when the new one is missing; otherwise each old value is taken where the new value is still the default.</summary>
    internal static void MergeSettings(string legacyDir, string newDir)
    {
        var oldPath = Path.Combine(legacyDir, "settings.json"); var newPath = Path.Combine(newDir, "settings.json");
        if (!File.Exists(oldPath)) return;
        if (!File.Exists(newPath)) { WalletSettings.CopyWritable(oldPath, newPath); return; }
        JsonObject? oldJ, newJ;
        try { oldJ = JsonNode.Parse(File.ReadAllText(oldPath)) as JsonObject; newJ = JsonNode.Parse(File.ReadAllText(newPath)) as JsonObject; }
        catch (JsonException) { return; }
        if (oldJ is null || newJ is null) return;
        var defJ = JsonNode.Parse(JsonSerializer.Serialize(new WalletSettings(), WalletSettings.JsonOptions)) as JsonObject;
        var changed = false;
        foreach (var (k, v) in oldJ)
        {
            var nv = newJ[k]; var dv = defJ?[k];
            var newIsDefault = nv is null || (dv is not null && JsonNode.DeepEquals(nv, dv));
            if (newIsDefault && v is not null && !JsonNode.DeepEquals(nv, v)) { newJ[k] = v.DeepClone(); changed = true; }
        }
        if (changed) File.WriteAllText(newPath, newJ.ToJsonString(WalletSettings.JsonOptions));
    }

    public static string NoticeText(Status st) => st.HasImportable
        ? $"The old folder\n{st.LegacyDir}\ncontains wallet file(s) that this version does not show: {string.Join(", ", st.ImportableWallets)}.\n\n" +
          (st.Conflicts.Count > 0
              ? $"A DIFFERENT wallet with the same name already exists in the new folder ({string.Join(", ", st.Conflicts)}). The old one will be imported under a new name (wallet.<network>.legacy-<id>.json); nothing is overwritten.\n\n"
              : "") +
          "Import them into the new folder now? Nothing is overwritten. Old settings (e.g. RPC timeout) are imported where you have not changed them."
        : $"Your wallet data is in\n{st.NewDir}.\n\nThe old folder\n{st.LegacyDir}\nstill contains an ENCRYPTED COPY of your wallet. If you change your password, the old copy keeps the OLD password; " +
          "if that password ever leaks, the old copy is a risk.\n\nDelete the old copy now? (Make sure you have your recovery phrase backed up.)" +
          (st.MigratedThisRun || st.DeclinedDeletes == 0 ? "" : "\n\n(Reminder — you will not be asked again.)");

    public static string ConflictHint(IReadOnlyDictionary<string, string> imported) =>
        string.Join("\n", imported.Where(kv => kv.Key != kv.Value).Select(kv =>
            $"{kv.Key} was imported as {kv.Value}. To use it, close the app and rename it to {kv.Key} (move the current {kv.Key} aside first)."));

    private static int ReadDeclines(string newDir)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(newDir, ReminderFile)))?["declinedDeletes"]?.GetValue<int>() ?? 0; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Records that the user declined deleting the old copy (drives the single later reminder).</summary>
    public static void RecordDeclined(Status st)
    {
        Directory.CreateDirectory(st.NewDir);
        File.WriteAllText(Path.Combine(st.NewDir, ReminderFile), new JsonObject { ["declinedDeletes"] = st.DeclinedDeletes + 1 }.ToJsonString());
    }

    /// <summary>Deletes the legacy folder. Requires confirmation; refuses if both folders are the same, or if any old wallet lacks a
    /// byte-identical copy (SHA-256) in the new folder.</summary>
    public static void DeleteLegacy(Status st, bool userConfirmed)
    {
        if (!userConfirmed) throw new InvalidOperationException("Deleting the old wallet copy requires explicit user confirmation.");
        if (st.SameFolder) throw new InvalidOperationException("Old and new data folders are the same folder; nothing was deleted.");
        if (!Directory.Exists(st.LegacyDir)) return;
        var have = NewHashes(st.NewDir);
        var missing = WalletFiles(st.LegacyDir).Where(f => !have.Contains(Hash(Path.Combine(st.LegacyDir, f)))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Not deleted: the new folder has no identical copy of {string.Join(", ", missing)}. Import first.");
        foreach (var f in Directory.GetFiles(st.LegacyDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(st.LegacyDir, recursive: true);
        WalletLog.Warn("Legacy data folder deleted at the user's request.");
    }
}
