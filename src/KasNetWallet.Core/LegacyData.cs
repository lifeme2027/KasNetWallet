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
        IReadOnlyList<string> ImportableWallets, IReadOnlyList<string> Conflicts, int DeclinedDeletes, bool SwitchesToMainnet = false, bool MainnetEnabledAfterImport = false)
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
        return new Status(legacyDir, newDir, migratedThisRun, legacy, importable, conflicts, ReadDeclines(newDir), WouldSwitchToMainnet(legacyDir, newDir),
            MainnetEnabledAfterMerge(legacyDir, newDir));
    }

    private static JsonObject? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Round 4: importing the old settings would make the app start on mainnet (old Network = mainnet, new settings has no Network key).</summary>
    internal static bool WouldSwitchToMainnet(string legacyDir, string newDir)
    {
        var oldNet = ReadJson(Path.Combine(legacyDir, "settings.json"))?["Network"];
        if (oldNet is null) return false;
        var text = oldNet.GetValueKind() == JsonValueKind.Number ? (oldNet.GetValue<int>() == (int)WalletNetwork.Mainnet ? "mainnet" : "") : oldNet.ToString();
        if (!text.Equals("mainnet", StringComparison.OrdinalIgnoreCase)) return false;
        var newJ = ReadJson(Path.Combine(newDir, "settings.json"));
        return newJ is null || !newJ.ContainsKey("Network");
    }

    /// <summary>M2: the MainnetEnabled value after <see cref="MergeSettings"/>: the new file's key if present, else the old one's, else false.</summary>
    internal static bool MainnetEnabledAfterMerge(string legacyDir, string newDir)
    {
        static bool? Read(JsonObject? j) => j?["MainnetEnabled"] is JsonNode n && n.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? n.GetValue<bool>() : null;
        var newJ = ReadJson(Path.Combine(newDir, "settings.json"));
        if (newJ is not null && newJ.ContainsKey("MainnetEnabled")) return Read(newJ) ?? false;
        return Read(ReadJson(Path.Combine(legacyDir, "settings.json"))) ?? false;
    }

    /// <summary>The network token of a wallet file name: wallet.&lt;token&gt;.json (e.g. testnet10, testnet10.legacy-1a2b3c4d).</summary>
    internal static string NetToken(string walletFile) => walletFile["wallet.".Length..^".json".Length];

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
            var net = NetToken(w); var newNet = NetToken(target);
            // N3: history follows the wallet under its new name; unlock-state only when the name is kept (a fresh counter is safer).
            var companions = target == w ? new[] { "history", "unlock-state" } : new[] { "history" };
            foreach (var kind in companions)
            {
                var src = Path.Combine(st.LegacyDir, $"{kind}.{net}.json"); var dst = Path.Combine(st.NewDir, $"{kind}.{newNet}.json");
                if (File.Exists(src) && !File.Exists(dst)) WalletSettings.CopyWritable(src, dst);
            }
            done[w] = target;
        }
        MergeSettings(st.LegacyDir, st.NewDir);
        WalletLog.Warn($"Imported {done.Count} wallet file(s) from the legacy data folder ({done.Count(kv => kv.Key != kv.Value)} renamed).");
        return done;
    }

    /// <summary>Old settings.json: copied when the new one is missing; otherwise only keys ABSENT from the new file are taken.
    /// A key present in the new file was saved by this version, i.e. set by the user, even if it equals the default (round 4).</summary>
    internal static void MergeSettings(string legacyDir, string newDir)
    {
        var oldPath = Path.Combine(legacyDir, "settings.json"); var newPath = Path.Combine(newDir, "settings.json");
        if (!File.Exists(oldPath)) return;
        if (!File.Exists(newPath)) { WalletSettings.CopyWritable(oldPath, newPath); return; }
        var oldJ = ReadJson(oldPath); var newJ = ReadJson(newPath);
        if (oldJ is null || newJ is null) return;
        var changed = false;
        foreach (var (k, v) in oldJ)
            if (!newJ.ContainsKey(k) && v is not null) { newJ[k] = v.DeepClone(); changed = true; }
        if (changed) File.WriteAllText(newPath, newJ.ToJsonString(WalletSettings.JsonOptions));
    }

    /// <summary>M2: the note matches what actually happens: start on mainnet only when MainnetEnabled will also be true.</summary>
    internal static string MainnetNote(Status st) => !st.SwitchesToMainnet ? ""
        : st.MainnetEnabledAfterImport
            ? "\n\nNOTE: the old settings use MAINNET. After the import the app will start on MAINNET (real KAS)."
            : "\n\nNOTE: the old settings select MAINNET, but mainnet is not enabled in them (MainnetEnabled = false). After the import the network setting will be MAINNET, " +
              "but the app will not connect to mainnet until you enable mainnet in the settings or switch back to testnet-10.";

    public static string NoticeText(Status st) => st.HasImportable
        ? $"The old folder\n{st.LegacyDir}\ncontains wallet file(s) that this version does not show: {string.Join(", ", st.ImportableWallets)}.\n\n" +
          (st.Conflicts.Count > 0
              ? $"A DIFFERENT wallet with the same name already exists in the new folder ({string.Join(", ", st.Conflicts)}). The old one will be imported under a new name (wallet.<network>.legacy-<id>.json); nothing is overwritten.\n\n"
              : "") +
          "Import them into the new folder now? Nothing is overwritten. Old settings (e.g. RPC timeout) are imported only where the new version has no saved value." +
          MainnetNote(st)
        : $"Your wallet data is in\n{st.NewDir}.\n\nThe old folder\n{st.LegacyDir}\nstill contains an ENCRYPTED COPY of your wallet. If you change your password, the old copy keeps the OLD password; " +
          "if that password ever leaks, the old copy is a risk.\n\nDelete the old copy now? (Make sure you have your recovery phrase backed up.)" +
          (File.Exists(Path.Combine(st.LegacyDir, "settings.json")) ? "\nOld settings are first merged into the new settings (only values the new version has not saved)." : "") + KeptText(PlanDelete(st).Keep) +
          (st.MigratedThisRun || st.DeclinedDeletes == 0 ? "" : "\n\n(Reminder — you will not be asked again.)");

    public static string ConflictHint(IReadOnlyDictionary<string, string> imported) =>
        string.Join("\n", imported.Where(kv => kv.Key != kv.Value).Select(kv =>
            $"{kv.Key} was imported as {kv.Value}. To use it, close the app and rename it to {kv.Key} (move the current {kv.Key} aside first)" +
            $"; if history.{NetToken(kv.Value)}.json exists, rename it to history.{NetToken(kv.Key)}.json the same way."));

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

    public sealed record DeletePlan(IReadOnlyList<string> Delete, IReadOnlyList<string> Keep);

    /// <summary>N1: what DeleteLegacy removes. Only top-level files: wallets with a byte-identical copy in the new folder, history
    /// files with a byte-identical copy in the new folder, unlock-state.*.json, settings.json (already merged) and the reminder file.
    /// Everything else (sub-folders such as backup/, *.bak, logs, unknown files) is kept. Names are relative to the old folder.</summary>
    public static DeletePlan PlanDelete(Status st)
    {
        var del = new List<string>(); var keep = new List<string>();
        if (!Directory.Exists(st.LegacyDir)) return new DeletePlan(del, keep);
        var newHashes = Directory.Exists(st.NewDir) ? Directory.GetFiles(st.NewDir).Select(Hash).ToHashSet() : new HashSet<string>();
        foreach (var d in Directory.GetDirectories(st.LegacyDir).Select(Path.GetFileName).OfType<string>().OrderBy(x => x, StringComparer.Ordinal))
            keep.Add(d + Path.DirectorySeparatorChar);
        foreach (var path in Directory.GetFiles(st.LegacyDir).OrderBy(x => x, StringComparer.Ordinal))
        {
            var f = Path.GetFileName(path);
            bool Json(string prefix) => f.StartsWith(prefix, StringComparison.Ordinal) && f.EndsWith(".json", StringComparison.Ordinal);
            var ok = (Json("wallet.") || Json("history.")) ? newHashes.Contains(Hash(path))
                : Json("unlock-state.") || f == "settings.json" || f == ReminderFile;
            (ok ? del : keep).Add(f);
        }
        return new DeletePlan(del, keep);
    }

    public static string KeptText(IReadOnlyList<string> kept) => kept.Count == 0 ? ""
        : $"\n\nThese items in the old folder are NOT deleted (not verified copies of wallet data): {string.Join(", ", kept)}.";

    /// <summary>N1: deletes only the files in <see cref="PlanDelete"/>; never recursive. Removes the old folder only if it ends up empty.
    /// Requires confirmation; refuses if both folders are the same, or if any old wallet lacks a byte-identical copy (SHA-256)
    /// in the new folder. Returns the plan that was carried out (Keep = what is left).</summary>
    public static DeletePlan DeleteLegacy(Status st, bool userConfirmed)
    {
        if (!userConfirmed) throw new InvalidOperationException("Deleting the old wallet copy requires explicit user confirmation.");
        if (st.SameFolder) throw new InvalidOperationException("Old and new data folders are the same folder; nothing was deleted.");
        if (!Directory.Exists(st.LegacyDir)) return new DeletePlan(Array.Empty<string>(), Array.Empty<string>());
        var have = NewHashes(st.NewDir);
        var missing = WalletFiles(st.LegacyDir).Where(f => !have.Contains(Hash(Path.Combine(st.LegacyDir, f)))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Not deleted: the new folder has no identical copy of {string.Join(", ", missing)}. Import first.");
        // M1: settings.json is in the delete plan, so merge it first (only absent keys; same rule as Import).
        // If the merge fails, the exception aborts the delete and nothing is lost.
        MergeSettings(st.LegacyDir, st.NewDir);
        var plan = PlanDelete(st);
        foreach (var f in plan.Delete)
        {
            var p = Path.Combine(st.LegacyDir, f);
            File.SetAttributes(p, FileAttributes.Normal);
            File.Delete(p);
        }
        if (plan.Keep.Count == 0 && !Directory.EnumerateFileSystemEntries(st.LegacyDir).Any()) Directory.Delete(st.LegacyDir, recursive: false);
        WalletLog.Warn($"Legacy data folder: deleted {plan.Delete.Count} verified file(s) at the user's request; kept {plan.Keep.Count} item(s).");
        return plan;
    }
}
