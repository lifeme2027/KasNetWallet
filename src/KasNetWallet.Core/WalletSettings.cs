using System.Text.Json.Nodes;
using System.Text.Json;

namespace KasNetWallet.Core;

/// <summary>Non-secret settings, stored as JSON next to the keystore.</summary>
public sealed class WalletSettings
{
    /// <summary>Default is testnet-10. Mainnet must be chosen explicitly (and <see cref="MainnetEnabled"/> set).</summary>
    public WalletNetwork Network { get; set; } = WalletNetwork.Testnet10;
    /// <summary>Explicit opt-in switch; without it the UI refuses to select mainnet.</summary>
    public bool MainnetEnabled { get; set; }
    public string? WrpcUrl { get; set; }
    /// <summary>Per-network mass policy (Toccata before/after). See <see cref="MassPolicy"/>.</summary>
    public Dictionary<WalletNetwork, MassPolicy> MassPolicies { get; set; } = new();
    /// <summary>Fee warning policy (N3): no hard fixed-amount cap; abnormal fees need explicit acknowledgement.</summary>
    public Transfer.FeePolicy FeePolicy { get; set; } = new();
    public int RpcTimeoutSeconds { get; set; } = 15;
    public int ClipboardClearSeconds { get; set; } = 30;
    public int AutoLockMinutes { get; set; } = 5;
    /// <summary>DAA-score depth after which an accepted tx is shown as confirmed.</summary>
    public ulong ConfirmationDepthDaa { get; set; } = 100;

    public MassPolicy MassPolicyFor(WalletNetwork n) => MassPolicies.TryGetValue(n, out var p) ? p : MassPolicy.DefaultFor(n);

    public string EffectiveWrpcUrl => string.IsNullOrWhiteSpace(WrpcUrl) ? Network.DefaultWrpcUrl() : WrpcUrl!;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new TolerantNetworkConverter() } };
    internal static JsonSerializerOptions JsonOptions => Opts;

    /// <summary>Current product data folder name (D20: renamed from KaspaDesktopWallet).</summary>
    public const string DirectoryName = "KasNetWallet";
    /// <summary>Pre-rename data folder name; still read via <see cref="MigrateLegacyDirectory"/>.</summary>
    public const string LegacyDirectoryName = "KaspaDesktopWallet";

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// %APPDATA%/KasNetWallet. On first access, if it does not exist yet but the legacy
    /// %APPDATA%/KaspaDesktopWallet does, the legacy files are copied over (see D20).
    /// </summary>
    public static string DefaultDirectory
    {
        get
        {
            if (_resolvedDir is not null) return _resolvedDir;
            _resolvedDir = ResolveDirectory(AppData, out var reason);
            LegacyFallbackReason = reason;
            return _resolvedDir;
        }
    }

    /// <summary>
    /// D20: one-time, non-destructive migration. If <paramref name="newDir"/> does not exist and
    /// <paramref name="legacyDir"/> does, copies all files (byte-for-byte, keystore format unchanged)
    /// into a temp folder and then renames it to <paramref name="newDir"/>. The legacy folder is left
    /// untouched as a backup. Returns true when a migration happened.
    /// </summary>
    public static bool MigrateLegacyDirectory(string legacyDir, string newDir)
    {
        if (Directory.Exists(newDir) || !Directory.Exists(legacyDir)) return false;
        var tmp = newDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".migrating";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); // N10: a leftover from an interrupted run is discarded
        try
        {
            CopyTree(legacyDir, tmp);
            SanitizeMigratedSettings(Path.Combine(tmp, "settings.json"));
            Directory.Move(tmp, newDir);
        }
        catch
        {   // N10: never leave KasNetWallet.migrating behind
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        LastMigratedFrom = legacyDir;
        WalletLog.Warn("Migrated data folder from legacy KaspaDesktopWallet location (legacy copy kept).");
        return true;
    }

    /// <summary>N9: the first-run copy follows D27. Network and MainnetEnabled are removed (default testnet-10; mainnet must be chosen
    /// and confirmed again), and the file must pass <see cref="ValidateTypes"/> (N7). A file that cannot be parsed or fails the check is not
    /// carried over; it stays in the legacy folder, whose delete/import prompt then reports it (N4), so nothing is lost silently.</summary>
    internal static void SanitizeMigratedSettings(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var j = ParseObject(File.ReadAllText(path));
            foreach (var k in j.Select(kv => kv.Key).Where(k => LegacyData.NotImportedKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray()) j.Remove(k);
            ValidateTypes(j, "old");
            File.WriteAllText(path, j.ToJsonString(Opts));
        }
        catch (Exception ex) when (IsBadSettings(ex))
        {
            File.Delete(path);
            WalletLog.Warn("Legacy settings.json was not migrated (unreadable or wrong value types); defaults are used and the legacy copy is kept.");
        }
    }

    /// <summary>N10: every exception System.Text.Json can raise for malformed settings (duplicate keys → ArgumentException,
    /// Network 1.5 / 99999999999 → FormatException/OverflowException, wrong types → JsonException/InvalidOperationException).</summary>
    public static bool IsBadSettings(Exception ex) =>
        ex is JsonException or ArgumentException or FormatException or OverflowException or InvalidOperationException or NotSupportedException;

    /// <summary>N10: strict parse of a settings file: must be one JSON object without duplicate key names (exact match; System.Text.Json
    /// would otherwise throw ArgumentException later, on first access). Throws JsonException.</summary>
    internal static JsonObject ParseObject(string text)
    {
        var j = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not a JSON object");
        try { _ = j.Count; foreach (var kv in j) { } }
        catch (ArgumentException ex) { throw new JsonException("duplicate key: " + ex.Message, ex); }
        return j;
    }

    /// <summary>N7: the settings object must deserialize with the same options as <see cref="Load"/> (value types, nested objects).
    /// Throws InvalidOperationException naming the problem. Range checks stay in <see cref="Normalize"/>.</summary>
    internal static void ValidateTypes(JsonObject settings, string what, string? fileToFix = null)
    {
        try { _ = settings.Deserialize<WalletSettings>(Opts); }
        catch (Exception ex) when (IsBadSettings(ex))
        { throw new InvalidOperationException($"The {what} settings would not load: {ex.Message} Fix or remove {fileToFix ?? $"the {what} settings.json"}, then try again.", ex); }
    }

    /// <summary>N10/N13: migrate if needed; on any failure fall back to the legacy folder and report why (<paramref name="fallbackReason"/>).</summary>
    internal static string ResolveDirectory(string appData, out string? fallbackReason)
    {
        fallbackReason = null;
        var dir = Path.Combine(appData, DirectoryName);
        var legacy = Path.Combine(appData, LegacyDirectoryName);
        try { MigrateLegacyDirectory(legacy, dir); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {   // N10: a failed first-run copy never crashes start-up; the temp folder is already removed by MigrateLegacyDirectory.
            WalletLog.Warn($"Could not migrate legacy data folder: {ex.GetType().Name}");
            if (Directory.Exists(legacy) && !Directory.Exists(dir))
            { fallbackReason = ex.GetType().Name + ": " + SecretRedactor.Redact(ex.Message); return legacy; }
        }
        return dir;
    }

    private static string? _resolvedDir; // N13: resolved once per process so the wallet and settings folders never diverge

    /// <summary>N13: set when <see cref="DefaultDirectory"/> fell back to the legacy folder because migration failed.</summary>
    public static string? LegacyFallbackReason { get; internal set; }

    /// <summary>N13: not persisted. When set, <see cref="Save"/> does not write (the legacy settings.json is never rewritten).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool ReadOnlySession { get; private set; }

    /// <summary>N13: when running from the legacy folder after a failed migration, start on testnet-10 with mainnet disabled
    /// (in memory only; the old file is not changed). Choosing mainnet asks for confirmation again. Returns the notice, or null.</summary>
    public string? ApplyStartupSafety(string? fallbackReason)
    {
        if (fallbackReason is null) return null;
        Network = WalletNetwork.Testnet10; MainnetEnabled = false; WrpcUrl = null; ReadOnlySession = true;
        WalletLog.Warn("Running from the legacy data folder (migration failed); forced testnet-10, settings read-only.");
        return "The data folder could not be moved to the new KasNetWallet location (" + fallbackReason + ").\n" +
               "The app is using the old KaspaDesktopWallet folder. For safety it starts on testnet-10 with mainnet disabled, " +
               "and settings changes are not saved to the old settings.json. To use mainnet, choose it again and confirm.\n" +
               "Fix the cause (permissions, a locked KasNetWallet.migrating folder, free space) and restart to finish the move.";
    }

    /// <summary>G1: set when <see cref="MigrateLegacyDirectory"/> copied the legacy folder in this process (UI shows a notice).</summary>
    public static string? LastMigratedFrom { get; internal set; }

    /// <summary>Legacy folder path (%APPDATA%/KaspaDesktopWallet).</summary>
    public static string LegacyDirectory => Path.Combine(AppData, LegacyDirectoryName);

    /// <summary>G5: copies a file and makes the copy writable by the owner (read-only source files would otherwise
    /// make unlock-state/settings unwritable in the new folder).</summary>
    internal static void CopyWritable(string src, string dst, bool overwrite = false)
    {
        File.Copy(src, dst, overwrite);
        var attr = File.GetAttributes(dst);
        if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, attr & ~FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dst, File.GetUnixFileMode(dst) | UnixFileMode.UserWrite | UnixFileMode.UserRead);
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) CopyWritable(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public static WalletSettings Load(string path) => Load(path, out _);

    public static WalletSettings Load(string path, out IReadOnlyList<string> notices)
    {
        notices = Array.Empty<string>();
        if (!File.Exists(path)) return new WalletSettings();
        WalletSettings s;
        try { s = JsonSerializer.Deserialize<WalletSettings>(File.ReadAllText(path), Opts) ?? new WalletSettings(); }
        catch (Exception ex) when (IsBadSettings(ex) || ex is IOException or UnauthorizedAccessException)
        {
            WalletLog.Warn("settings.json is unreadable; using defaults (testnet-10, mainnet disabled).");
            notices = new[] { "settings.json was unreadable; defaults are used (testnet-10, mainnet disabled)." };
            return new WalletSettings();
        }
        var n = s.Normalize();
        foreach (var x in n) WalletLog.Warn("settings: " + x);
        notices = n;
        return s;
    }

    /// <summary>N6: range-validates every value; out-of-range values are reset to the default and reported.</summary>
    public IReadOnlyList<string> Normalize()
    {
        var d = new WalletSettings();
        var notes = new List<string>();
        void Fix<T>(string name, T value, bool ok, Action reset, string range)
        { if (!ok) { reset(); notes.Add($"{name}={value} is outside {range}; reset to default."); } }
        Fix(nameof(Network), TolerantNetworkConverter.Describe(Network), Enum.IsDefined(Network), () => Network = d.Network, "known networks");
        Fix(nameof(RpcTimeoutSeconds), RpcTimeoutSeconds, RpcTimeoutSeconds is >= 1 and <= 300, () => RpcTimeoutSeconds = d.RpcTimeoutSeconds, "1..300");
        Fix(nameof(ClipboardClearSeconds), ClipboardClearSeconds, ClipboardClearSeconds is >= 5 and <= 600, () => ClipboardClearSeconds = d.ClipboardClearSeconds, "5..600");
        Fix(nameof(AutoLockMinutes), AutoLockMinutes, AutoLockMinutes is >= 1 and <= 1440, () => AutoLockMinutes = d.AutoLockMinutes, "1..1440");
        Fix(nameof(ConfirmationDepthDaa), ConfirmationDepthDaa, ConfirmationDepthDaa is >= 1 and <= 1_000_000, () => ConfirmationDepthDaa = d.ConfirmationDepthDaa, "1..1000000");
        if (WrpcUrl is { Length: > 0 } u && !(Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme is "ws" or "wss")))
        { notes.Add("WrpcUrl is not a ws:// or wss:// URL; using the network default."); WrpcUrl = null; }
        FeePolicy ??= new();
        var fp = FeePolicy; var dp = new Transfer.FeePolicy();
        Fix("FeePolicy.RateMultiplier", fp.RateMultiplier, double.IsFinite(fp.RateMultiplier) && fp.RateMultiplier >= 1.5 && fp.RateMultiplier <= 1000, () => fp.RateMultiplier = dp.RateMultiplier, "1.5..1000");
        Fix("FeePolicy.WarnPercent", fp.WarnPercent, double.IsFinite(fp.WarnPercent) && fp.WarnPercent >= 0.01 && fp.WarnPercent <= 100, () => fp.WarnPercent = dp.WarnPercent, "0.01..100");
        Fix("FeePolicy.WarnFloorSompi", fp.WarnFloorSompi, fp.WarnFloorSompi >= 10_000 && fp.WarnFloorSompi <= 100_000_000_000, () => fp.WarnFloorSompi = dp.WarnFloorSompi, "10000..100000000000");
        MassPolicies ??= new();
        foreach (var (net, mp) in MassPolicies.ToArray())
        {
            if (!Enum.IsDefined(net)) { MassPolicies.Remove(net); notes.Add("MassPolicies contains an unknown network; entry removed."); continue; }
            if (mp is null) { MassPolicies.Remove(net); notes.Add($"MassPolicies[{net}] was null; removed."); continue; }
            if (mp.MaxTransactionMass is ulong m && (m < 5_000 || m > 1_000_000))
            { mp.MaxTransactionMass = null; notes.Add($"MassPolicies[{net}].MaxTransactionMass={m} is outside 5000..1000000; reset."); }
        }
        return notes;
    }

    public void Save(string path)
    {
        if (ReadOnlySession) { WalletLog.Warn("settings not saved (read-only legacy session, N13)."); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}
