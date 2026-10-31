using KasNetWallet.Core;
using KasNetWallet.Core.Rpc;
using KasNetWallet.Core.Transfer;
using NKaspa;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Fixes for kdw-0c7e556-retest F1–F5.</summary>
public class Retest0c7e556Tests
{
    private static HdAccount Tn() => HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
    private const ulong Kas1 = 100_000_000;

    // ---------- F1 ----------
    private sealed class OversizeThenOk : IWalletNode
    {
        private readonly bool _bad;
        public OversizeThenOk(bool bad) => _bad = bad;
        public Task<NodeStatus> GetStatusAsync(CancellationToken ct = default) =>
            _bad ? throw new InvalidOperationException("WebSocket message exceeds the maximum of 16777216 bytes.")
                 : Task.FromResult(new NodeStatus("f", "testnet-10", true, true, 500_000_000));
        public Task<Money> GetBalanceAsync(string a, CancellationToken ct = default) => throw new InvalidOperationException("WebSocket is not connected.");
        public Task<IReadOnlyList<Coin>> GetCoinsAsync(string a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<double?> GetNormalFeeRateAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> SubmitAsync(Transaction tx, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task F1_OversizedFrame_TriggersReconnect()
    {
        var made = 0;
        var node = new ResilientWalletNode(_ => Task.FromResult<IWalletNode>(new OversizeThenOk(made++ == 0)), delay: (_, _) => Task.CompletedTask);
        var st = await node.GetStatusAsync();
        Assert.Equal(500_000_000UL, st.VirtualDaaScore);
        Assert.Equal(2, node.Reconnects);
    }

    [Theory]
    [InlineData("WebSocket message exceeds the maximum of 16777216 bytes.", true)]
    [InlineData("WebSocket is not connected.", true)]
    [InlineData("Sequence contains no elements", false)]
    public void F1_DeadConnectionClassification(string msg, bool dead) =>
        Assert.Equal(dead, ResilientWalletNode.IsDeadConnection(new InvalidOperationException(msg)));

    // ---------- F2 ----------
    [Fact]
    public void F2_ChangeNearMinimum_AddsInputInsteadOfBlocking()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 10 * Kas1, 1), TestData.FakeCoin(acc.Address, Kas1 / 2, 2) };
        foreach (var change in new ulong[] { 1_958_800, 1_980_000, 2_000_004 })
        {
            var plan = TransferService.Build(acc, coins, TestData.AbandonTestnet, 10 * Kas1 - 203_600 - change);
            Assert.Equal(0UL, plan.FoldedChangeSompi);
            Assert.Equal(1, plan.AddedInputsForChange);
            Assert.True(plan.ChangeSompi >= 2_000_000);
        }
    }

    [Fact]
    public void F2_ChangeNearMinimum_SingleUtxo_BlockMessageIsAccurate()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 10 * Kas1, 1) };
        var ex = Assert.ThrowsAny<TransferException>(() => TransferService.Build(acc, coins, TestData.AbandonTestnet, 10 * Kas1 - 203_600 - 1_980_000));
        Assert.Contains("too small to be its own output", ex.Message);
        Assert.DoesNotContain("change output exceeds", ex.Message);
    }

    // ---------- F3 ----------
    private static int Outcome(Func<TransferPlan> f)
    {
        try { var p = f(); return p.FoldedChangeSompi > 0 ? 0 : 2; }
        catch (TransferException) { return 1; }
    }

    [Fact]
    public void F3_PreToccata_BoundaryIsMonotonicAndDeterministic()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 10 * Kas1, 1) };
        var pre = new MassPolicy { ToccataActive = false };
        var last = -1;
        // amount decreasing => change increasing: outcomes must go fold(0) → block(1) → ok(2), never back.
        for (ulong change = 9_900_000; change <= 10_020_000; change += 1_000)
        {
            var amt = 10 * Kas1 - 2_036 - change;
            var o = Outcome(() => TransferService.Build(acc, coins, TestData.AbandonTestnet, amt, massPolicy: pre));
            Assert.Equal(o, Outcome(() => TransferService.Build(acc, coins, TestData.AbandonTestnet, amt, massPolicy: pre)));
            Assert.True(o >= last, $"non-monotonic at change={change}: {last}→{o}");
            last = o;
        }
        for (ulong change = 9_997_900; change <= 10_000_100; change += 7)
        {
            var o = Outcome(() => TransferService.Build(acc, coins, TestData.AbandonTestnet, 10 * Kas1 - 2_036 - change, massPolicy: pre));
            Assert.NotEqual(0, o); // inside the guard band nothing is silently folded
        }
    }

    // ---------- F4 ----------
    [Fact]
    public void F4_UnsyncedOrStaleNode_IsBlocked()
    {
        var ok = new NodeStatus("f", "testnet-10", true, true, 611_072_766);
        WalletManager.EnsureNodeFresh(ok, WalletNetwork.Testnet10, 611_000_000);
        Assert.Contains("not synced", Assert.Throws<TransferException>(() => WalletManager.EnsureNodeFresh(ok with { IsSynced = false }, WalletNetwork.Testnet10, 0)).Message);
        Assert.Contains("out of date", Assert.Throws<TransferException>(() => WalletManager.EnsureNodeFresh(ok with { VirtualDaaScore = 0 }, WalletNetwork.Testnet10, 0)).Message);
        Assert.Contains("out of date", Assert.Throws<TransferException>(() => WalletManager.EnsureNodeFresh(ok with { VirtualDaaScore = 467_579_631 }, WalletNetwork.Testnet10, 0)).Message);
        Assert.Contains("seen earlier", Assert.Throws<TransferException>(() => WalletManager.EnsureNodeFresh(ok, WalletNetwork.Testnet10, 611_072_766 + WalletManager.DaaRegressionTolerance + 1)).Message);
        Assert.Throws<TransferException>(() => WalletManager.EnsureNodeFresh(ok with { VirtualDaaScore = 474_165_564 }, WalletNetwork.Mainnet, 0));
    }

    // ---------- F5 ----------
    [Theory]
    [InlineData("\u200b")] [InlineData("\u200c")] [InlineData("\u200d")] [InlineData("\ufeff")] [InlineData("\u00ad")]
    public void F5_ZeroWidth_RejectedWithClearMessage(string zw)
    {
        var bad = TestData.AbandonMnemonic.Replace("about", "ab" + zw + "out");
        var ex = Assert.Throws<FormatException>(() => MnemonicService.Normalize(bad));
        Assert.Contains("invisible character", ex.Message);
        Assert.DoesNotContain("abandon", ex.Message);
        Assert.Throws<FormatException>(() => MnemonicService.Normalize(zw + TestData.AbandonMnemonic));
    }
}
