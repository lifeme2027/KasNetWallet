using KasNetWallet.Core;
using KasNetWallet.Core.Transfer;
using NKaspa;
using Xunit;
using WMass = KasNetWallet.Core.MassPolicy;

namespace KasNetWallet.Core.Tests;

/// <summary>Adaptation to NKaspa 5c49251 (offline, no broadcast).</summary>
public class NKaspa5c49251Tests
{
    private static HdAccount Tn() => HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);

    [Fact]
    public void MassPolicy_Auto_UsesDaaScore()
    {
        var p = new WMass();
        Assert.True(p.WithDaaScore(Network.TestnetToccataActivationDaaScore).Resolve(Network.Testnet).ToccataActive);
        Assert.False(p.WithDaaScore(Network.TestnetToccataActivationDaaScore - 1).Resolve(Network.Testnet).ToccataActive);
        Assert.True(p.Resolve(Network.Testnet).ToccataActive); // no DAA: tn10 has activated
        var main = p.Resolve(Network.Mainnet);             // no DAA: conservative mainnet mix
        Assert.False(main.ToccataActive);
        Assert.Equal(100UL, main.MinimumRelayFeeRate);
        // dea2697: the wallet never uses the 1 sompi/gram pre-Toccata relay rate (current nodes may reject it)
        Assert.Equal(100UL, new WMass { ToccataActive = false }.Resolve(Network.Testnet).MinimumRelayFeeRate);
        Assert.Equal(100UL, p.WithDaaScore(Network.TestnetToccataActivationDaaScore - 1).Resolve(Network.Testnet).MinimumRelayFeeRate);
    }

    [Fact]
    public void MinimumRelayFee_FollowsPolicyRate()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 5 * Kas.SompiPerKas, 1) };
        var post = TransferService.Build(acc, coins, TestData.AbandonTestnet, Kas.SompiPerKas);
        var pre = TransferService.Build(acc, coins, TestData.AbandonTestnet, Kas.SompiPerKas, massPolicy: new WMass { ToccataActive = false });
        Assert.Equal(post.ExpectedFeeSompi, post.FeeSompi);
        Assert.Equal(pre.ExpectedFeeSompi, pre.FeeSompi);
        Assert.Equal(post.FeeSompi, pre.FeeSompi); // dea2697 adaptation: forced pre-Toccata keeps the 100 sompi/gram rate
    }

    [Fact]
    public void Mnemonic_BadChecksum_Rejected_ByNKaspaAndWallet()
    {
        var bad = string.Join(' ', Enumerable.Repeat("abandon", 12));
        Assert.Throws<FormatException>(() => Mnemonic.Parse(bad));
        Assert.Throws<FormatException>(() => MnemonicService.Normalize(bad));
        Assert.ThrowsAny<Exception>(() => new Mnemonic(bad).DeriveSeed());
    }

    [Fact]
    public void Mnemonic_ZeroWidth_StillRejectedByWallet()
    {
        var zw = TestData.AbandonMnemonic.Replace("about", "ab\u200bout");
        Assert.Throws<FormatException>(() => MnemonicService.Normalize(zw));
    }

    [Fact]
    public void Generate_UsesToPhrase_Valid()
    {
        var p = MnemonicService.Generate(12);
        Assert.Equal(12, p.Split(' ').Length);
        Assert.True(MnemonicService.IsValid(p));
    }

    [Fact]
    public void DefaultJson_RefusesSecrets()
    {
        using var acc = Tn();
        var m = new Mnemonic(TestData.AbandonMnemonic);
        Assert.ThrowsAny<Exception>(() => System.Text.Json.JsonSerializer.Serialize(m));
        Assert.DoesNotContain("abandon", m.ToString() ?? "");
    }
}
