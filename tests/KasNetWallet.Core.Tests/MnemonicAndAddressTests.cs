using KasNetWallet.Core;

namespace KasNetWallet.Core.Tests;

public class MnemonicAndAddressTests
{
    [Fact]
    public void KnownVector_Mainnet_ReceiveAddress() =>
        Assert.Equal(TestData.AbandonMainnet, HdAccount.DeriveAddress(TestData.AbandonMnemonic, WalletNetwork.Mainnet));

    [Fact]
    public void KnownVector_Testnet10_ReceiveAddress() =>
        Assert.Equal(TestData.AbandonTestnet, HdAccount.DeriveAddress(TestData.AbandonMnemonic, WalletNetwork.Testnet10));

    [Fact]
    public void Normalize_TrimsAndLowercases() =>
        Assert.Equal(TestData.AbandonMnemonic, MnemonicService.Normalize("  ABANDON abandon  abandon abandon abandon abandon abandon abandon abandon abandon abandon About \n"));

    [Theory]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")] // bad checksum
    [InlineData("abandon abandon abandon")]
    [InlineData("notaword abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    [InlineData("")]
    public void Invalid_Mnemonics_Rejected(string m) => Assert.False(MnemonicService.IsValid(m));

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    public void Generate_ProducesValidMnemonic(int words)
    {
        var m = MnemonicService.Generate(words);
        Assert.Equal(words, m.Split(' ').Length);
        Assert.True(MnemonicService.IsValid(m));
        Assert.StartsWith("kaspa:", HdAccount.DeriveAddress(m, WalletNetwork.Mainnet));
    }

    [Fact]
    public void Generate_IsRandom() => Assert.NotEqual(MnemonicService.Generate(), MnemonicService.Generate());

    [Fact]
    public void Bip39Passphrase_ChangesAddress() =>
        Assert.NotEqual(TestData.AbandonMainnet, HdAccount.DeriveAddress(TestData.AbandonMnemonic, WalletNetwork.Mainnet, "TREZOR"));

    [Fact]
    public void Lock_DisposesKey()
    {
        var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Mainnet);
        acc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => TransferHelpers.Build(acc));
    }
}

internal static class TransferHelpers
{
    public static object Build(HdAccount acc) =>
        KasNetWallet.Core.Transfer.TransferService.Build(acc, new[] { TestData.FakeCoin(acc.Address, 100_000_000, 1) }, TestData.OtherMainnet, 10_000_000);
}
