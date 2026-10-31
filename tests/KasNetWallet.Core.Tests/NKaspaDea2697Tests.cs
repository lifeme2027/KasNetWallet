using KasNetWallet.Core;
using KasNetWallet.Core.Transfer;
using NKaspa;
using Xunit;
using WMass = KasNetWallet.Core.MassPolicy;

namespace KasNetWallet.Core.Tests;

/// <summary>Adaptation to NKaspa dea2697 (0.1.10-alpha): ChangeFoldedIntoFee, 1 sompi/gram pre-Toccata relay rate never used.</summary>
public class NKaspaDea2697Tests
{
    private static HdAccount Tn() => HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);

    [Fact]
    public void Pin_And_Constants()
    {
        Assert.Equal(1UL, NKaspa.Policy.MassPolicy.PreToccataMinimumRelayFeeRate); // NKaspa itself still offers 1
        Assert.Equal(467_579_632UL, Network.TestnetToccataActivationDaaScore);
        Assert.Equal(476_232_000UL, Network.TestnetZkHardeningActivationDaaScore);
    }

    [Theory]
    [InlineData(null, 611_000_000UL, true)]
    [InlineData(false, 611_000_000UL, true)]   // forced pre-Toccata is overridden by a live DAA after activation
    [InlineData(null, 467_579_632UL, true)]    // activation score itself
    [InlineData(null, 470_000_000UL, true)]    // between Toccata and ZK hardening: Toccata activation is the switch
    [InlineData(true, 1UL, true)]
    [InlineData(null, null, true)]             // tn10 without DAA
    [InlineData(false, null, false)]
    [InlineData(null, 467_579_631UL, false)]
    public void Resolve_PicksPostToccataByDaa_AndNeverRate1(bool? forced, ulong? daa, bool expectPost)
    {
        foreach (var net in new[] { Network.Testnet, Network.Mainnet })
        {
            var p = new WMass { ToccataActive = forced }.WithDaaScore(daa);
            var r = p.Resolve(net);
            Assert.True(r.MinimumRelayFeeRate >= 100, $"{net.Name} forced={forced} daa={daa}: rate {r.MinimumRelayFeeRate}");
            if (net == Network.Testnet) Assert.Equal(expectPost, r.ToccataActive);
        }
        Assert.True(new WMass { ToccataActive = false }.WithDaaScore(Network.MainnetToccataActivationDaaScore).Resolve(Network.Mainnet).ToccataActive);
    }

    [Fact]
    public void WalletManager_DaaPath_UsesPostToccata_ForLiveTn10()
    {
        var p = new WMass().WithDaaScore(611_072_766);
        var b = p.CreateBuilder(Network.Testnet);
        Assert.Equal(100UL, b.MinimumRelayFeeRate);
        Assert.Equal(NKaspa.Policy.MassPolicy.PostToccata.MaximumTransactionMass, p.EffectiveCap(Network.Testnet));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Build_FeeRateNeverBelow100(bool? forced)
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 5 * Kas.SompiPerKas, 1) };
        var p = TransferService.Build(acc, coins, TestData.AbandonTestnet, Kas.SompiPerKas, massPolicy: new WMass { ToccataActive = forced });
        Assert.True(p.FeeSompi >= 100UL * 1_000, $"fee {p.FeeSompi}");
        Assert.Equal(p.ExpectedFeeSompi, p.FeeSompi);
    }

    [Fact]
    public void ChangeFoldedIntoFee_DustFold_StillReportedAsFold()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 10 * Kas.SompiPerKas, 1) };
        var p = TransferService.Build(acc, coins, TestData.AbandonTestnet, 10 * Kas.SompiPerKas - 203_600 - 500_000);
        Assert.True(p.FoldedChangeSompi > 0);
        Assert.Equal(0UL, p.ChangeSompi);
    }
}
