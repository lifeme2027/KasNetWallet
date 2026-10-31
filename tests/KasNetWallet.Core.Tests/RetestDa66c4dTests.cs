using System.Text.Json.Nodes;
using KasNetWallet.Core;
using KasNetWallet.Core.Transfer;
using NKaspa;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Fixes for kasnetwallet-da66c4d-retest: S5, S6, N-G2 (NodeEstimate), D-btn, S7 + follow-ups, docs.</summary>
public class RetestDa66c4dTests
{
    private static HdAccount Tn() => HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
    private const ulong Kas1 = 100_000_000;
    private sealed class Dirs : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "kdw-s-" + Guid.NewGuid().ToString("N"));
        public string Old => Path.Combine(Root, "KaspaDesktopWallet");
        public string New => Path.Combine(Root, "KasNetWallet");
        public Dirs() { Directory.CreateDirectory(Old); Directory.CreateDirectory(New); }
        public void W(string dir, string f, string c) => File.WriteAllText(Path.Combine(dir, f), c);
        public string R(string dir, string f) => File.ReadAllText(Path.Combine(dir, f));
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    // ---------- S5 ----------
    [Fact]
    public void S5_SameNameDifferentContent_DeleteRefused_ImportRenames_NeverOverwrites()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "WALLET-A"); d.W(d.Old, "wallet.mainnet.json", "M");
        d.W(d.New, "wallet.testnet10.json", "WALLET-B");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.Equal(new[] { "wallet.mainnet.json", "wallet.testnet10.json" }, st.ImportableWallets);
        Assert.Equal(new[] { "wallet.testnet10.json" }, st.Conflicts);
        Assert.Contains("DIFFERENT wallet with the same name", LegacyData.NoticeText(st));
        // tester's S5 sequence: delete without importing A must be refused
        var ex = Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(st, userConfirmed: true));
        Assert.Contains("wallet.testnet10.json", ex.Message);
        Assert.Equal("WALLET-A", d.R(d.Old, "wallet.testnet10.json"));
        var map = LegacyData.Import(st);
        Assert.Equal("WALLET-B", d.R(d.New, "wallet.testnet10.json"));
        var renamed = map["wallet.testnet10.json"];
        Assert.Matches(@"^wallet\.testnet10\.legacy-[0-9a-f]{8}\.json$", renamed);
        Assert.Equal("WALLET-A", d.R(d.New, renamed));
        Assert.Equal("M", d.R(d.New, "wallet.mainnet.json"));
        Assert.Contains("rename it", LegacyData.ConflictHint(map));
        // now every old wallet has an identical copy → delete allowed
        var after = LegacyData.Inspect(d.Old, d.New, true);
        Assert.False(after.HasImportable);
        LegacyData.DeleteLegacy(after, userConfirmed: true);
        Assert.False(Directory.Exists(d.Old));
    }

    [Fact]
    public void S5_ModifiedAfterImport_DeleteRefused()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A");
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        d.W(d.Old, "wallet.testnet10.json", "A2"); // old copy changed later
        Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, true), true));
        Assert.True(File.Exists(Path.Combine(d.Old, "wallet.testnet10.json")));
    }

    [Fact]
    public void S5_ImportTwice_IsIdempotent()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); d.W(d.New, "wallet.testnet10.json", "B");
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        var again = LegacyData.Inspect(d.Old, d.New, false);
        Assert.False(again.HasImportable);
        Assert.Empty(LegacyData.Import(again));
        Assert.Equal(2, Directory.GetFiles(d.New, "wallet.*.json").Length);
    }

    // ---------- S6 ----------
    [Fact]
    public void S6_OnlyTn10InOld_DifferentFromNew_IsPrompted()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); d.W(d.New, "wallet.testnet10.json", "B");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.True(st.HasImportable);
        Assert.Equal(new[] { "wallet.testnet10.json" }, st.ImportableWallets);
        Assert.False(st.ShouldOfferDelete);
    }

    [Fact]
    public void S6_IdenticalCopy_NotImportable_ButDeleteOffered()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); d.W(d.New, "wallet.testnet10.json", "A");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.False(st.HasImportable);
        Assert.True(st.ShouldOfferDelete);
    }

    // ---------- item 5: reminder, settings, same folder ----------
    [Fact]
    public void Reminder_AfterDecline_OfferedOnceMore_ThenNot()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); d.W(d.New, "wallet.testnet10.json", "A");
        var first = LegacyData.Inspect(d.Old, d.New, true);
        Assert.True(first.ShouldOfferDelete);
        LegacyData.RecordDeclined(first);
        var second = LegacyData.Inspect(d.Old, d.New, false);
        Assert.True(second.ShouldOfferDelete);
        Assert.Contains("Reminder", LegacyData.NoticeText(second));
        LegacyData.RecordDeclined(second);
        Assert.False(LegacyData.Inspect(d.Old, d.New, false).ShouldOfferDelete);
        Assert.True(Directory.Exists(d.Old));
    }

    [Fact]
    public void Import_BringsSettings_IncludingRpcTimeout()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A");
        new WalletSettings { RpcTimeoutSeconds = 42, WrpcUrl = "ws://127.0.0.1:17210" }.Save(Path.Combine(d.Old, "settings.json"));
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"));
        Assert.Equal(42, s.RpcTimeoutSeconds);
        Assert.Equal("ws://127.0.0.1:17210", s.WrpcUrl);
    }

    [Fact]
    public void Import_MergesSettings_KeepsUserChangedNewValues()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A");
        new WalletSettings { RpcTimeoutSeconds = 42, AutoLockMinutes = 9 }.Save(Path.Combine(d.Old, "settings.json"));
        new WalletSettings { AutoLockMinutes = 3 }.Save(Path.Combine(d.New, "settings.json")); // RpcTimeout still default
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"));
        Assert.Equal(42, s.RpcTimeoutSeconds);
        Assert.Equal(3, s.AutoLockMinutes);
    }

    [Fact]
    public void S7_SameFolder_DeleteAndImportRefused()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A");
        var st = LegacyData.Inspect(d.Old, d.Old + Path.DirectorySeparatorChar, true);
        Assert.True(st.SameFolder);
        Assert.False(st.ShouldOfferDelete);
        Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(st, true));
        Assert.Throws<InvalidOperationException>(() => LegacyData.Import(st));
        Assert.True(File.Exists(Path.Combine(d.Old, "wallet.testnet10.json")));
    }

    // ---------- D-btn ----------
    [Fact]
    public void Dbtn_CancelIsDefault_WithAndWithoutSuggestion()
    {
        Assert.Equal(3, ChangeBlockedException.CancelButtonNumber(true));  // Yes / No / Cancel
        Assert.Equal(2, ChangeBlockedException.CancelButtonNumber(false)); // OK / Cancel
    }

    // ---------- N-G2: NodeEstimate post-Toccata ----------
    private static int Outcome(HdAccount acc, Coin[] coins, ulong amt, double rate, out ChangeBlockedException? blocked)
    {
        blocked = null;
        try
        {
            var p = TransferService.Build(acc, coins, TestData.AbandonTestnet, amt, FeeMode.NodeEstimate, rate, massPolicy: new MassPolicy { ToccataActive = true });
            return p.FoldedChangeSompi > 0 ? 0 : 2;
        }
        catch (ChangeBlockedException ex) { blocked = ex; return 1; }
    }

    [Theory]
    [InlineData(102.33, 10UL)]
    [InlineData(150.0, 10UL)]
    [InlineData(1000.0, 10UL)]
    [InlineData(102.33, 1UL)]
    public void NG2_NodeEstimate_PostToccata_MonotonicNoErrors_SuggestionsBuild(double rate, ulong balanceKas)
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, balanceKas * Kas1, 1) };
        var fee = (ulong)Math.Ceiling(rate) * 2_036UL;
        var last = -1; var blocks = 0; var suggested = 0;
        for (var change = 1_000_000UL; change <= 25_000_000UL; change += 20_000)
        {
            if (balanceKas * Kas1 <= fee + change + TransferService.MinPaymentSompi) break;
            var o = Outcome(acc, coins, balanceKas * Kas1 - fee - change, rate, out var b); // any other exception fails the test ("did not converge")
            Assert.True(o >= last, $"rate={rate}: non-monotonic at change={change}: {last}->{o}");
            last = o;
            if (b is not null)
            {
                blocks++;
                if (b.SuggestedAmountSompi is ulong s)
                {
                    suggested++;
                    var ok = TransferService.Build(acc, coins, TestData.AbandonTestnet, s, FeeMode.NodeEstimate, rate, massPolicy: new MassPolicy { ToccataActive = true });
                    Assert.Equal(0UL, ok.FoldedChangeSompi);
                    Assert.True(ok.ChangeSompi > 0);
                }
            }
        }
        Assert.Equal(2, last);
        Assert.Equal(blocks, suggested);
        Assert.True((ulong)blocks * 20_000UL <= 1_000_000UL, $"blocked band too wide: {blocks} steps"); // same narrow band as MinimumRelay
    }

    [Fact]
    public void NG2_NodeEstimate_SmallChange_NoLongerBlocked_FeeNotInflated()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 10 * Kas1, 1) };
        var p = TransferService.Build(acc, coins, TestData.AbandonTestnet, 10 * Kas1 - 103UL * 2_036UL - 12_000_000UL, FeeMode.NodeEstimate, 102.33, massPolicy: new MassPolicy { ToccataActive = true });
        Assert.True(p.ChangeSompi > 11_000_000);
        Assert.True(p.FeeSompi < 1_000_000, $"fee {p.FeeSompi}");
    }

    // ---------- docs ----------
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KasNetWallet.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }

    [Theory]
    [InlineData("NOTICE")] [InlineData("README.md")] [InlineData("docs/DECISIONS.md")] [InlineData("docs/FOR-TESTERS.md")] [InlineData("src/KasNetWallet.WinForms/Theme.cs")]
    public void Docs_DoNotClaimPlenteumOrigin(string rel) =>
        Assert.DoesNotContain("plenteum", RepoFile(rel), StringComparison.OrdinalIgnoreCase);
}
