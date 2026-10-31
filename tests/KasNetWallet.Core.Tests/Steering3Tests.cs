using KasNetWallet.Core;
using KasNetWallet.Core.Keystore;
using KasNetWallet.Core.Rpc;
using KasNetWallet.Core.Tracking;
using KasNetWallet.Core.Transfer;
using NKaspa;

namespace KasNetWallet.Core.Tests;

public class SettingsAndMnemonicTests
{
    [Fact]
    public void DefaultNetwork_IsTestnet10_AndMainnetDisabled()
    {
        var s = new WalletSettings();
        Assert.Equal(WalletNetwork.Testnet10, s.Network);
        Assert.False(s.MainnetEnabled);
        Assert.Null(s.MassPolicyFor(WalletNetwork.Testnet10).ToccataActive); // auto (DAA score)
        Assert.True(s.MassPolicyFor(WalletNetwork.Testnet10).Resolve(NKaspa.Network.Testnet).ToccataActive); // tn10 activated
    }

    [Fact]
    public async Task Mainnet_Connect_RequiresExplicitEnable()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await using var m = new WalletManager(dir, new WalletSettings { Network = WalletNetwork.Mainnet }, KdfParameters.TestOnly);
            await Assert.ThrowsAsync<InvalidOperationException>(() => m.ConnectAsync());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("  ABANDON abandon\tabandon abandon\nabandon abandon abandon abandon abandon abandon abandon About ")]
    [InlineData("abandon\u3000abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    public void Mnemonic_WhitespaceAndCase_Normalize(string phrase) =>
        Assert.Equal(TestData.AbandonMnemonic, MnemonicService.Normalize(phrase));

    [Theory]
    [InlineData("abandon\u200b abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    [InlineData("\ufeffabandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    [InlineData("abandn abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    public void Mnemonic_InvisibleOrMisspelled_Rejected(string phrase)
    {
        var ex = Assert.Throws<FormatException>(() => MnemonicService.Normalize(phrase));
        Assert.Contains("Word #1", ex.Message);
    }

    [Fact]
    public void ChangePassword_ReencryptsAndOldFails()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var m = new WalletManager(dir, new WalletSettings(), KdfParameters.TestOnly);
            m.CreateOrImport(TestData.AbandonMnemonic, "old-password-1");
            m.ChangePassword("old-password-1", "new-password-2");
            Assert.Throws<WrongPasswordException>(() => m.Unlock("old-password-1"));
            m.Unlock("new-password-2");
            Assert.Equal(TestData.AbandonTestnet, m.Address);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmptyPassword_Forbidden()
    {
        Assert.Throws<ArgumentException>(() => WalletManager.NormalizePassword(""));
        Assert.Throws<ArgumentException>(() => WalletManager.NormalizePassword("short"));
        // NFKC: full-width and ASCII forms unlock the same keystore
        Assert.Equal(WalletManager.NormalizePassword("ｐａｓｓｗｏｒｄ１"), WalletManager.NormalizePassword("password1"));
    }
}

public class RedactionTests
{
    [Fact]
    public void Redactor_MasksMnemonic_Xprv_Hex()
    {
        using var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
        var mnemonic = new Mnemonic(TestData.AbandonMnemonic);
        var xprv = mnemonic.DeriveExtKey().ExportXprv(); // 5c49251: ToString() no longer prints the xprv
        var hex = Convert.ToHexString(new byte[32].Select((_, i) => (byte)(i * 7 + 1)).ToArray());
        var text = $"oops mnemonic={TestData.AbandonMnemonic} key={xprv} raw={hex} addr={acc.Address}";
        var r = SecretRedactor.Redact(text);
        Assert.DoesNotContain("abandon abandon", r);
        Assert.DoesNotContain(xprv, r);
        Assert.DoesNotContain(hex, r);
        Assert.Contains(acc.Address, r); // addresses stay readable
    }

    [Fact]
    public void Redactor_LeavesNormalText()
    {
        const string s = "Connected to node testnet-10, balance 12.5 KAS, the wallet is about ready";
        Assert.Equal(s, SecretRedactor.Redact(s));
    }

    [Fact]
    public void WalletLog_AlwaysRedacts()
    {
        var lines = new List<string>();
        WalletLog.Sink = lines.Add;
        try
        {
            WalletLog.Error("failed", new InvalidOperationException("bad phrase " + TestData.AbandonMnemonic));
            var line = Assert.Single(lines.ToArray(), l => l.Contains("ERROR failed"));
            Assert.DoesNotContain("abandon abandon", line);
            Assert.Contains(SecretRedactor.Mask, line);
        }
        finally { WalletLog.Sink = null; }
    }
}

public class ResilienceTests
{
    private sealed class FlakyNode : IWalletNode
    {
        public bool Fail;
        public int Submits;
        public Task<NodeStatus> GetStatusAsync(CancellationToken ct = default) =>
            Fail ? throw new System.Net.WebSockets.WebSocketException("down") : Task.FromResult(new NodeStatus("f", "testnet-10", true, true, 5));
        public Task<Money> GetBalanceAsync(string a, CancellationToken ct = default) => Task.FromResult(Money.FromSompi(1));
        public Task<IReadOnlyList<Coin>> GetCoinsAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Coin>>(Array.Empty<Coin>());
        public Task<double?> GetNormalFeeRateAsync(CancellationToken ct = default) => Task.FromResult<double?>(1);
        public Task<string> SubmitAsync(Transaction tx, CancellationToken ct = default) { Submits++; throw new IOException("dropped"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HangingNode : IWalletNode
    {
        public async Task<NodeStatus> GetStatusAsync(CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return null!; }
        public Task<Money> GetBalanceAsync(string a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Coin>> GetCoinsAsync(string a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<double?> GetNormalFeeRateAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> SubmitAsync(Transaction tx, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Reconnects_WithExponentialBackoff()
    {
        var connects = 0;
        var node = new ResilientWalletNode(_ => { connects++; return Task.FromResult<IWalletNode>(new FlakyNode { Fail = connects < 3 }); },
            TimeSpan.FromSeconds(5), maxAttempts: 5, baseDelay: TimeSpan.FromSeconds(1), delay: (_, _) => Task.CompletedTask);
        var st = await node.GetStatusAsync();
        Assert.Equal("testnet-10", st.NetworkId);
        Assert.Equal(3, connects);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, node.Backoffs);
    }

    [Fact]
    public async Task GivesUp_AfterMaxAttempts()
    {
        var node = new ResilientWalletNode(_ => Task.FromResult<IWalletNode>(new FlakyNode { Fail = true }),
            maxAttempts: 3, delay: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<TimeoutException>(() => node.GetStatusAsync());
        Assert.Equal(2, node.Backoffs.Count);
    }

    [Fact]
    public async Task Timeout_IsEnforced()
    {
        var node = new ResilientWalletNode(_ => Task.FromResult<IWalletNode>(new HangingNode()),
            TimeSpan.FromMilliseconds(100), maxAttempts: 1);
        await Assert.ThrowsAsync<TimeoutException>(() => node.GetStatusAsync());
    }

    [Fact]
    public async Task Submit_IsNotRetried()
    {
        var inner = new FlakyNode();
        var node = new ResilientWalletNode(_ => Task.FromResult<IWalletNode>(inner), maxAttempts: 5, delay: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<TimeoutException>(() => node.SubmitAsync(new Transaction()));
        Assert.Equal(1, inner.Submits);
    }

    [Theory]
    [InlineData("http://127.0.0.1:17210")]
    [InlineData("ws://127.0.0.1:16210")]
    [InlineData("not a url")]
    public void Url_Validation_RejectsNonWrpc(string url) =>
        Assert.Throws<ArgumentException>(() => ResilientWalletNode.ValidateUrl(url));
}

public class TrackerTests
{
    private static Coin C(string txHexSeed, uint idx, ulong daa, byte seed)
    {
        var b = new byte[32]; for (var i = 0; i < 32; i++) b[i] = (byte)(seed + i);
        var spk = KaspaAddress.Parse(TestData.AbandonTestnet).ScriptPubKey;
        return new Coin(new OutPoint(new uint256(b), idx), new UtxoEntry(100UL, spk, daa, false));
    }

    [Fact]
    public void Pending_Accepted_Confirmed_Reorged()
    {
        var input = C("", 0, 10, 1);
        var change = C("", 1, 1000, 50);
        var t = new TxTracker(confirmationDepth: 100, dropAfterDaa: 600);
        var tx = t.Add(change.Outpoint.TransactionId.ToString(), "x", 1, 1, new[] { input.Outpoint }, hasChange: true, daa: 990);

        t.Recheck(Array.Empty<Coin>(), 995);                // input spent in mempool, change not yet visible
        Assert.Equal(TxState.Pending, tx.State);
        t.Recheck(new[] { change }, 1050);
        Assert.Equal(TxState.Accepted, tx.State);
        Assert.Equal(50UL, tx.Confirmations);
        t.Recheck(new[] { change }, 1100);
        Assert.Equal(TxState.Confirmed, tx.State);
        t.Recheck(new[] { input }, 1110);                   // reorg: input is back, change gone
        Assert.Equal(TxState.Reorged, tx.State);
        t.Recheck(new[] { change }, 1300);                  // re-included in another block
        Assert.Equal(TxState.Confirmed, tx.State);
    }

    [Fact]
    public void NeverIncluded_BecomesDropped_AndPersists()
    {
        var input = C("", 0, 10, 1);
        var t = new TxTracker(100, 600);
        var tx = t.Add("ab".PadLeft(64, '0'), "x", 1, 1, new[] { input.Outpoint }, true, 1000);
        t.Recheck(new[] { input }, 1200);
        Assert.Equal(TxState.Pending, tx.State);
        t.Recheck(new[] { input }, 1600);
        Assert.Equal(TxState.Dropped, tx.State);

        var path = Path.GetTempFileName();
        try { t.Save(path); var t2 = new TxTracker(); t2.Load(path); Assert.Equal(TxState.Dropped, t2.Items[0].State); }
        finally { File.Delete(path); }
    }
}

public class MassPolicyTests
{
    private static Coin[] Coins(string addr, int n) =>
        Enumerable.Range(0, n).Select(i => TestData.FakeCoin(addr, Kas.SompiPerKas, (byte)(i % 200), (uint)i)).ToArray();

    [Fact]
    public void PreToccataCap_RejectsLargeTx_PostToccataAccepts()
    {
        using var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
        var coins = Coins(acc.Address, 150);
        var amount = 140 * Kas.SompiPerKas;
        Assert.ThrowsAny<TransferException>(() =>
            TransferService.Build(acc, coins, TestData.AbandonTestnet, amount, massPolicy: new MassPolicy { ToccataActive = false }));
        var plan = TransferService.Build(acc, coins, TestData.AbandonTestnet, amount, massPolicy: new MassPolicy { ToccataActive = true });
        Assert.True(plan.Mass > 100_000);
    }

    [Fact]
    public void ExplicitMaxMass_Override()
    {
        using var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
        Assert.Throws<TransferException>(() => TransferService.Build(acc, Coins(acc.Address, 10), TestData.AbandonTestnet,
            9 * Kas.SompiPerKas, massPolicy: new MassPolicy { ToccataActive = true, MaxTransactionMass = 5000 }));
        Assert.Equal(5000UL, new MassPolicy { ToccataActive = true, MaxTransactionMass = 5000 }.EffectiveCap(Network.Testnet));
    }

    [Theory]
    [InlineData("ws://eva.kaspa.green:17110", 18110)]
    [InlineData("ws://127.0.0.1:17210", 18210)]
    [InlineData("wss://node.example:17310", 18310)]
    public void D32_BorshPortsRejectedWithJsonHint(string url, int jsonPort)
    {
        var ex = Assert.Throws<ArgumentException>(() => ResilientWalletNode.ValidateUrl(url));
        Assert.Contains("Borsh", ex.Message);
        Assert.Contains(jsonPort.ToString(), ex.Message);
    }

    [Theory]
    [InlineData("ws://eva.kaspa.green:18110")]
    [InlineData("ws://127.0.0.1:18210")]
    public void D32_JsonPortsAccepted(string url) => Assert.Equal(new Uri(url).ToString(), ResilientWalletNode.ValidateUrl(url));
}
