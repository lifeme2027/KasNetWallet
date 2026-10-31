using NKaspa;

namespace KasNetWallet.Core;

/// <summary>Networks the wallet supports. Default is configurable in <see cref="WalletSettings"/>.</summary>
public enum WalletNetwork
{
    Mainnet,
    Testnet10,
}

public static class WalletNetworkExtensions
{
    /// <summary>Maps to the NKaspa network (address prefix, HD versions). Testnet-10 uses the "testnet" prefix (kaspatest:).</summary>
    public static Network ToNKaspa(this WalletNetwork n) => n switch
    {
        WalletNetwork.Mainnet => Network.Mainnet,
        WalletNetwork.Testnet10 => Network.Testnet,
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };

    /// <summary>The network id string expected from getServerInfo (e.g. "mainnet", "testnet-10").</summary>
    public static string ExpectedNodeNetworkId(this WalletNetwork n) => n switch
    {
        WalletNetwork.Mainnet => "mainnet",
        WalletNetwork.Testnet10 => "testnet-10",
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };

    public static string DefaultWrpcUrl(this WalletNetwork n) => n switch
    {
        // Public community endpoints; users should prefer their own node. See docs/DECISIONS.md (D3).
        WalletNetwork.Mainnet => "wss://eva.kaspa.green/kaspa/mainnet/wrpc/json",
        WalletNetwork.Testnet10 => "ws://127.0.0.1:18210",
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };
}
