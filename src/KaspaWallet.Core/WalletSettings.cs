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
    /// <summary>Fee cap: max(MaxFeeFloorSompi, amount × MaxFeePercent / 100). Transfers above it are rejected (KDW-11).</summary>
    public ulong MaxFeeFloorSompi { get; set; } = 1_000_000; // 0.01 KAS
    public double MaxFeePercent { get; set; } = 1.0;
    public ulong FeeCapFor(ulong amountSompi) =>
        Math.Max(MaxFeeFloorSompi, (ulong)Math.Min(ulong.MaxValue / 2, amountSompi * Math.Max(0, MaxFeePercent) / 100.0));
    public int RpcTimeoutSeconds { get; set; } = 15;
    public int ClipboardClearSeconds { get; set; } = 30;
    public int AutoLockMinutes { get; set; } = 5;
    /// <summary>DAA-score depth after which an accepted tx is shown as confirmed.</summary>
    public ulong ConfirmationDepthDaa { get; set; } = 100;

    public MassPolicy MassPolicyFor(WalletNetwork n) => MassPolicies.TryGetValue(n, out var p) ? p : MassPolicy.DefaultFor(n);

    public string EffectiveWrpcUrl => string.IsNullOrWhiteSpace(WrpcUrl) ? Network.DefaultWrpcUrl() : WrpcUrl!;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaspaDesktopWallet");

    public static WalletSettings Load(string path)
    {
        if (!File.Exists(path)) return new WalletSettings();
        try { return JsonSerializer.Deserialize<WalletSettings>(File.ReadAllText(path), Opts) ?? new WalletSettings(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            WalletLog.Warn("settings.json is unreadable; using defaults (testnet-10, mainnet disabled).");
            return new WalletSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}
