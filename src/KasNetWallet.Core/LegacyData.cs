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
        IReadOnlyList<string> ImportableWallets, IReadOnlyList<string> Conflicts, int DeclinedDeletes, bool OldUsesMainnet = false, string? NewSavedNetwork = null, string? SettingsError = null)
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
        return new Status(legacyDir, newDir, migratedThisRun, legacy, importable, conflicts, ReadDeclines(newDir), OldSettingsUseMainnet(legacyDir),
            NetworkText(ReadJson(Path.Combine(newDir, "settings.json"))?["Network"]), SettingsMergeError(legacyDir, newDir));
    }

    private static JsonObject? ReadJson(string path)
    {
        try { return File.Exists(path) ? WalletSettings.ParseObject(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || WalletSettings.IsBadSettings(ex)) { return null; } // N10
    }

    /// <summary>"mainnet", "testnet10", … for a Network value (string or enum number); null when absent.</summary>
    /// N10: numbers use TryGetValue (1.5 or 99999999999 are reported as written, never thrown).
    private static string? NetworkText(JsonNode? n) => n is null ? null
        : n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? (v.TryGetValue<int>(out var i) && Enum.IsDefined(typeof(WalletNetwork), i) ? ((WalletNetwork)i).ToString() : v.ToJsonString())
        : n.ToString();

    /// <summary>N12: the value of a key, matched case-insensitively (first match).</summary>
    private static JsonNode? GetCi(JsonObject? j, string key) => j?.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The old settings choose mainnet (string or enum number). Informational only: Network is never imported (N6).</summary>
    internal static bool OldSettingsUseMainnet(string legacyDir) =>
        string.Equals(NetworkText(GetCi(ReadJson(Path.Combine(legacyDir, "settings.json")), "Network")), nameof(WalletNetwork.Mainnet), StringComparison.OrdinalIgnoreCase);

    /// <summary>N4: why <see cref="MergeSettings"/> would fail right now (shown in the prompt), or null.</summary>
    internal static string? SettingsMergeError(string legacyDir, string newDir)
    {
        try { PlanMerge(legacyDir, newDir); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
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
        PlanMerge(st.LegacyDir, st.NewDir); // N4/N7: fail before copying anything if a settings file is broken or the merge would not load
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

    /// <summary>N6: keys never imported. Network + MainnetEnabled are the mainnet choice and its consent; they must be made in this
    /// version (StartForm asks), so a merge can never make the next start connect to mainnet silently.</summary>
    internal static readonly string[] NotImportedKeys = { "Network", "MainnetEnabled" };

    /// <summary>N4: strict read of both settings files. Throws InvalidOperationException if a file that exists cannot be read or is not a
    /// JSON object (never returns "nothing to merge" for a broken file). Returns (old, new); old is null only when there is no old file.</summary>
    private static (JsonObject? Old, JsonObject? New) ReadForMerge(string legacyDir, string newDir)
    {
        static JsonObject? Strict(string path, string what)
        {
            if (!File.Exists(path)) return null;
            try { return WalletSettings.ParseObject(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || WalletSettings.IsBadSettings(ex))
            { throw new InvalidOperationException($"The {what} settings file {path} cannot be read ({ex.Message}). Fix or remove it, then try again.", ex); }
        }
        var oldJ = Strict(Path.Combine(legacyDir, "settings.json"), "old");
        var newJ = oldJ is null ? null : Strict(Path.Combine(newDir, "settings.json"), "new");
        return (oldJ, newJ);
    }

    /// <summary>Old settings.json → new: only keys ABSENT from the new file are taken (a key present in the new file was set by the user, round 4),
    /// and never <see cref="NotImportedKeys"/> (N6). N4: a broken or unreadable file throws; callers abort (nothing is deleted).</summary>
    internal static void MergeSettings(string legacyDir, string newDir)
    {
        var (target, changed) = PlanMerge(legacyDir, newDir);
        if (target is not null && changed) File.WriteAllText(Path.Combine(newDir, "settings.json"), target.ToJsonString(WalletSettings.JsonOptions));
    }

    /// <summary>Computes the merged settings without writing. N8: key names are compared case-insensitively, so a case variant
    /// (rpcTimeoutSeconds vs RpcTimeoutSeconds) is never added as a duplicate. N7: the result must deserialize as
    /// <see cref="WalletSettings"/>; a wrong value type throws InvalidOperationException (callers abort).</summary>
    internal static (JsonObject? Target, bool Changed) PlanMerge(string legacyDir, string newDir)
    {
        var (oldJ, newJ) = ReadForMerge(legacyDir, newDir);
        if (oldJ is null) return (null, false);
        var target = newJ?.DeepClone().AsObject() ?? new JsonObject();
        var changed = newJ is null;
        var have = new HashSet<string>(target.Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in oldJ)
            if (v is not null && !NotImportedKeys.Contains(k, StringComparer.OrdinalIgnoreCase) && have.Add(k)) { target[k] = v.DeepClone(); changed = true; }
        // N11: name the file that is actually wrong. The new file alone must load; if it does, a failure of the merge comes from the old values.
        if (newJ is not null) WalletSettings.ValidateTypes(newJ, "new", $"the new settings.json ({Path.Combine(newDir, "settings.json")})");
        WalletSettings.ValidateTypes(target, "merged", $"the old settings.json ({Path.Combine(legacyDir, "settings.json")})");
        return (target, changed);
    }

    /// <summary>M2/N5/N6: the old network choice is not imported, so the note only says what the app will use; it attributes nothing to the old
    /// file except its Network value.</summary>
    internal static string MainnetNote(Status st) => !st.OldUsesMainnet ? ""
        : "\n\nNOTE: the old settings used MAINNET. The network choice is not imported (nor MainnetEnabled): the app keeps " +
          (st.NewSavedNetwork is null ? "the default network, testnet-10" : $"the network saved in the new settings ({st.NewSavedNetwork})") +
          ". To use mainnet, choose it again in this version; it asks for confirmation.";

    /// <summary>N4: a settings file that cannot be read blocks the import/delete; say so up front.</summary>
    internal static string SettingsErrorNote(Status st) => st.SettingsError is null ? ""
        : $"\n\nWARNING: {st.SettingsError} Until then the old folder is not imported or deleted.";

    public static string NoticeText(Status st) => st.HasImportable
        ? $"The old folder\n{st.LegacyDir}\ncontains wallet file(s) that this version does not show: {string.Join(", ", st.ImportableWallets)}.\n\n" +
          (st.Conflicts.Count > 0
              ? $"A DIFFERENT wallet with the same name already exists in the new folder ({string.Join(", ", st.Conflicts)}). The old one will be imported under a new name (wallet.<network>.legacy-<id>.json); nothing is overwritten.\n\n"
              : "") +
          "Import them into the new folder now? Nothing is overwritten. Old settings (e.g. RPC timeout) are imported only where the new version has no saved value." +
          MainnetNote(st) + SettingsErrorNote(st)
        : $"Your wallet data is in\n{st.NewDir}.\n\nThe old folder\n{st.LegacyDir}\nstill contains an ENCRYPTED COPY of your wallet. If you change your password, the old copy keeps the OLD password; " +
          "if that password ever leaks, the old copy is a risk.\n\nDelete the old copy now? (Make sure you have your recovery phrase backed up.)" +
          (File.Exists(Path.Combine(st.LegacyDir, "settings.json")) ? "\nOld settings are first merged into the new settings (only values the new version has not saved)." : "") +
          MainnetNote(st) + SettingsErrorNote(st) + KeptText(PlanDelete(st).Keep) +
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
        // N4: a broken/unreadable settings file throws here, so the delete is aborted and nothing is lost.
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
