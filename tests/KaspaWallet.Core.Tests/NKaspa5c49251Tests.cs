using KaspaWallet.Core;
using KaspaWallet.Core.Transfer;
using NKaspa;
using Xunit;
using WMass = KaspaWallet.Core.MassPolicy;

namespace KaspaWallet.Core.Tests;

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
        Assert.Equal(1UL, new WMass { ToccataActive = false }.Resolve(Network.Testnet).MinimumRelayFeeRate);
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
        Assert.True(pre.FeeSompi * 50 < post.FeeSompi, $"pre={pre.FeeSompi} post={post.FeeSompi}");
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
