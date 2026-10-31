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
            var dir = Path.Combine(AppData, DirectoryName);
            try { MigrateLegacyDirectory(Path.Combine(AppData, LegacyDirectoryName), dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WalletLog.Warn($"Could not migrate legacy data folder: {ex.GetType().Name}");
                // Fall back to the legacy folder so existing wallets stay usable.
                var legacy = Path.Combine(AppData, LegacyDirectoryName);
                if (Directory.Exists(legacy) && !Directory.Exists(dir)) return legacy;
            }
            return dir;
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
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        CopyTree(legacyDir, tmp);
        Directory.Move(tmp, newDir);
        LastMigratedFrom = legacyDir;
        WalletLog.Warn("Migrated data folder from legacy KaspaDesktopWallet location (legacy copy kept).");
        return true;
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
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or IOException)
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
        Fix(nameof(Network), Network, Enum.IsDefined(Network), () => Network = d.Network, "known networks");
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}
