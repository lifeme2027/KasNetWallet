using KasNetWallet.Core;
using KasNetWallet.Core.Rpc;
using KasNetWallet.Core.Transfer;
using NKaspa;

namespace KasNetWallet.Core.Tests;

public class TransferTests
{
    private static HdAccount Acc() => HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Mainnet);

    [Fact]
    public void Build_SignsAndVerifies_WithMinimumRelayFee()
    {
        using var acc = Acc();
        var coin = TestData.FakeCoin(acc.Address, 5 * Kas.SompiPerKas, 7);
        var plan = TransferService.Build(acc, new[] { coin }, TestData.OtherMainnet, Kas.SompiPerKas);
        Assert.Equal(Kas.SompiPerKas, plan.AmountSompi);
        Assert.Equal(1, plan.InputCount);
        Assert.Equal(plan.InputSompi, plan.AmountSompi + plan.FeeSompi + plan.ChangeSompi);
        Assert.True(plan.FeeSompi > 0);
        Assert.Equal(2, plan.Transaction.Outputs.Count);
        Assert.True(plan.Transaction.Verify(new[] { coin.Utxo }));
        Assert.Equal(NKaspa.Policy.MassCalculator.MinimumRelayFee(NKaspa.Policy.MassCalculator.Calculate(plan.Transaction, new[] { coin.Utxo })), plan.FeeSompi);
    }

    [Fact]
    public void Build_IsDeterministic_TxIdAndFee_IndependentOfCoinOrder()
    {
        using var acc = Acc();
        var c1 = TestData.FakeCoin(acc.Address, 3 * Kas.SompiPerKas, 1);
        var c2 = TestData.FakeCoin(acc.Address, 3 * Kas.SompiPerKas, 9, 2);
        var a = TransferService.Build(acc, new[] { c1, c2 }, TestData.OtherMainnet, 4 * Kas.SompiPerKas);
        var b = TransferService.Build(acc, new[] { c2, c1 }, TestData.OtherMainnet, 4 * Kas.SompiPerKas);
        Assert.Equal(a.TransactionId, b.TransactionId); // Kaspa tx id excludes signature scripts
        Assert.Equal(a.FeeSompi, b.FeeSompi);
        Assert.Equal(a.Mass, b.Mass);
        Assert.Equal(2, a.InputCount);
        var byOutpoint = new[] { c1, c2 };
        var entries = b.Transaction.Inputs.Select(i => byOutpoint.Single(c => c.Outpoint.Equals(i.PreviousOutpoint)).Utxo).ToArray();
        Assert.True(b.Transaction.Verify(entries));
        Assert.True(a.Transaction.Verify(entries));
    }

    [Fact]
    public void Build_TamperedOutput_FailsVerification()
    {
        using var acc = Acc();
        var coin = TestData.FakeCoin(acc.Address, 5 * Kas.SompiPerKas, 3);
        var plan = TransferService.Build(acc, new[] { coin }, TestData.OtherMainnet, Kas.SompiPerKas);
        var hex = plan.Transaction.ToHex();
        var copy = Transaction.FromHex(hex);
        Assert.True(copy.Verify(new[] { coin.Utxo }));
        var other = new UtxoEntry(coin.Amount.Sompi + 1, coin.ScriptPubKey, 1000, false);
        Assert.False(copy.Verify(new[] { other })); // sighash commits to spent amount
    }

    [Fact]
    public void Build_InsufficientFunds_Throws()
    {
        using var acc = Acc();
        var coin = TestData.FakeCoin(acc.Address, Kas.SompiPerKas, 1);
        Assert.Throws<TransferException>(() => TransferService.Build(acc, new[] { coin }, TestData.OtherMainnet, Kas.SompiPerKas));
    }

    [Fact]
    public void Build_WrongNetworkRecipient_Throws()
    {
        using var acc = Acc();
        var coin = TestData.FakeCoin(acc.Address, 5 * Kas.SompiPerKas, 1);
        Assert.Throws<TransferException>(() => TransferService.Build(acc, new[] { coin }, TestData.AbandonTestnet, Kas.SompiPerKas));
        Assert.Throws<TransferException>(() => TransferService.Build(acc, new[] { coin }, "kaspa:garbage", Kas.SompiPerKas));
    }

    [Fact]
    public void Build_ForeignCoin_Throws()
    {
        using var acc = Acc();
        var coin = TestData.FakeCoin(TestData.OtherMainnet, 5 * Kas.SompiPerKas, 1);
        Assert.Throws<TransferException>(() => TransferService.Build(acc, new[] { coin }, TestData.OtherMainnet, Kas.SompiPerKas));
    }

    [Fact]
    public void Build_NodeEstimateBelowFloor_ClampedToFloor()
    {
        using var acc = Acc();
        var coin = TestData.FakeCoin(acc.Address, 5 * Kas.SompiPerKas, 1);
        var p = TransferService.Build(acc, new[] { coin }, TestData.OtherMainnet, Kas.SompiPerKas, FeeMode.NodeEstimate, nodeFeeRate: 1.0);
        Assert.True(p.FeeSompi >= 100 * p.Mass / 2); // floor rate applies
    }

    [Fact]
    public async Task Broadcast_RequiresConfirmation_AndMatchingNetwork()
    {
        using var acc = Acc();
        var coin = TestData.FakeCoin(acc.Address, 5 * Kas.SompiPerKas, 1);
        var plan = TransferService.Build(acc, new[] { coin }, TestData.OtherMainnet, Kas.SompiPerKas);
        var fake = new FakeNode("mainnet");
        await Assert.ThrowsAsync<TransferException>(() => TransferService.BroadcastAsync(fake, plan, userConfirmed: false));
        Assert.Equal(0, fake.Submitted);
        await Assert.ThrowsAsync<TransferException>(() => TransferService.BroadcastAsync(new FakeNode("testnet-10"), plan, true));
        var id = await TransferService.BroadcastAsync(fake, plan, true);
        Assert.Equal(plan.TransactionId, id);
        Assert.Equal(1, fake.Submitted);
        await Assert.ThrowsAsync<TransferException>(() => TransferService.BroadcastAsync(fake, plan, true)); // single use
    }

    [Theory]
    [InlineData("1", 100_000_000UL)]
    [InlineData("0.00000001", 1UL)]
    [InlineData("12.5", 1_250_000_000UL)]
    public void Kas_Parse(string s, ulong expected) => Assert.Equal(expected, Kas.ParseKas(s));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.000000001")]
    [InlineData("abc")]
    public void Kas_Parse_Invalid(string s) => Assert.Throws<FormatException>(() => Kas.ParseKas(s));

    [Fact]
    public void Kas_Format() => Assert.Equal("1.25000000", Kas.Format(125_000_000));

    /// <summary>In-memory node; never touches the network.</summary>
    internal sealed class FakeNode : IWalletNode
    {
        private readonly string _net;
        public FakeNode(string net) => _net = net;
        public int Submitted;
        public Task<NodeStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new NodeStatus("fake", _net, true, true, 1));
        public Task<Money> GetBalanceAsync(string address, CancellationToken ct = default) => Task.FromResult(Money.FromSompi(0));
        public Task<IReadOnlyList<Coin>> GetCoinsAsync(string address, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Coin>>(Array.Empty<Coin>());
        public Task<double?> GetNormalFeeRateAsync(CancellationToken ct = default) => Task.FromResult<double?>(1);
        public Task<string> SubmitAsync(Transaction tx, CancellationToken ct = default) { Submitted++; return Task.FromResult(tx.GetId().ToString()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
