using System.Text.Json;

namespace KaspaWallet.Core;

/// <summary>Non-secret settings, stored as JSON next to the keystore.</summary>
public sealed class WalletSettings
{
    public WalletNetwork Network { get; set; } = WalletNetwork.Mainnet;
    public string? WrpcUrl { get; set; }

    public string EffectiveWrpcUrl => string.IsNullOrWhiteSpace(WrpcUrl) ? Network.DefaultWrpcUrl() : WrpcUrl!;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaspaDesktopWallet");

    public static WalletSettings Load(string path)
    {
        if (!File.Exists(path)) return new WalletSettings();
        return JsonSerializer.Deserialize<WalletSettings>(File.ReadAllText(path), Opts) ?? new WalletSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}
