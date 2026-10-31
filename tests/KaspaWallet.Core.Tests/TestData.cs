using KaspaWallet.Core;
using NKaspa;

namespace KaspaWallet.Core.Tests;

internal static class TestData
{
    // Public BIP39 test vector mnemonic. Never use for funds.
    public const string AbandonMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    // Cross-checked against the independent kaswallet .NET port (KasWallet.Core.WalletService.GenerateReceiveAddress).
    public const string AbandonMainnet = "kaspa:qqd6e65yefepe9wk0m9vuxdufxd80sphy67gwwd0vdaumzdt4tc9s3qt0lqeh";
    public const string AbandonTestnet = "kaspatest:qqd6e65yefepe9wk0m9vuxdufxd80sphy67gwwd0vdaumzdt4tc9ssxd5s7gn";
    // Well-known valid mainnet address (from kaswallet/NKaspa test vectors).
    public const string OtherMainnet = "kaspa:qr0lr4ml9fn3chekrqmjdkergxl93l4wrk3dankcgvjq776s9wn9jkdskewva";

    public static Coin FakeCoin(string address, ulong sompi, byte seed, uint index = 0)
    {
        var bytes = new byte[32];
        for (var i = 0; i < 32; i++) bytes[i] = (byte)(seed + i);
        var spk = KaspaAddress.Parse(address).ScriptPubKey;
        return new Coin(new OutPoint(new uint256(bytes), index), new UtxoEntry(sompi, spk, 1000, false));
    }
}
