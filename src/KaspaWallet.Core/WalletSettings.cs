using System.Text.Json;

namespace KaspaWallet.Core;

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

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaspaDesktopWallet");

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
