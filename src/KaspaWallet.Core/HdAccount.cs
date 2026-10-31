using NKaspa;

namespace KaspaWallet.Core;

/// <summary>
/// Derived key material for the single MVP receive address m/44'/111111'/0'/0/0.
/// Holds a private <see cref="Key"/>; dispose (lock) to zero it.
/// </summary>
public sealed class HdAccount : IDisposable
{
    public const string ReceivePath = "m/44'/111111'/0'/0/0";

    private Key? _key;

    private HdAccount(Key key, WalletNetwork network)
    {
        _key = key;
        Network = network;
        Address = key.GetAddress(network.ToNKaspa()).ToString();
    }

    public WalletNetwork Network { get; }
    public string Address { get; }

    internal Key Key => _key ?? throw new ObjectDisposedException(nameof(HdAccount));

    public static HdAccount FromMnemonic(string mnemonic, WalletNetwork network, string? bip39Passphrase = null)
    {
        var m = new Mnemonic(MnemonicService.Normalize(mnemonic), Wordlist.English);
        using var master = m.DeriveExtKey(bip39Passphrase);
        using var receive = master.Derive(KeyPath.KaspaReceive(0));
        return new HdAccount(receive.PrivateKey, network);
    }

    /// <summary>Address only, without keeping the key alive.</summary>
    public static string DeriveAddress(string mnemonic, WalletNetwork network, string? bip39Passphrase = null)
    {
        using var acc = FromMnemonic(mnemonic, network, bip39Passphrase);
        return acc.Address;
    }

    public void Dispose()
    {
        _key?.Dispose();
        _key = null;
    }
}
