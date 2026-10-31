using KasNetWallet.Core;
using KasNetWallet.Core.Rpc;
using KasNetWallet.Core.Transfer;
using NKaspa;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Fixes for kasnetwallet-5747f6b-retest G1, G2 (F3), G3, G5.</summary>
public class Retest5747f6bTests
{
    private static HdAccount Tn() => HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
    private const ulong Kas1 = 100_000_000;
    private static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "kdw-g-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    // ---------- G1 ----------
    [Fact]
    public void G1_BothFoldersExist_LegacyWalletDetectedAndImported_NoOverwrite()
    {
        var root = Tmp();
        try
        {
            var legacy = Path.Combine(root, "KaspaDesktopWallet"); var nu = Path.Combine(root, "KasNetWallet");
            Directory.CreateDirectory(legacy); Directory.CreateDirectory(nu);
            File.WriteAllText(Path.Combine(legacy, "wallet.testnet10.json"), "T");
            File.WriteAllText(Path.Combine(legacy, "unlock-state.testnet10.json"), "U");
            File.WriteAllText(Path.Combine(legacy, "wallet.mainnet.json"), "OLD");
            File.WriteAllText(Path.Combine(nu, "wallet.mainnet.json"), "NEW");
            var st = LegacyData.Inspect(legacy, nu, migratedThisRun: false);
            Assert.True(st.HasImportable);
            // da66c4d S5: compared by content — the different mainnet wallet is importable too (under a new name)
            Assert.Equal(new[] { "wallet.mainnet.json", "wallet.testnet10.json" }, st.ImportableWallets);
            Assert.Contains("does not show", LegacyData.NoticeText(st));
            LegacyData.Import(st);
            Assert.Equal("T", File.ReadAllText(Path.Combine(nu, "wallet.testnet10.json")));
            Assert.Equal("U", File.ReadAllText(Path.Combine(nu, "unlock-state.testnet10.json")));
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(nu, "wallet.mainnet.json")));
            Assert.Single(Directory.GetFiles(nu, "wallet.mainnet.legacy-*.json"));
            Assert.False(LegacyData.Inspect(legacy, nu, false).HasImportable);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void G1_AfterMigration_NoticeWarnsOldPassword_DeleteNeedsConfirmation()
    {
        var root = Tmp();
        try
        {
            var legacy = Path.Combine(root, "KaspaDesktopWallet"); var nu = Path.Combine(root, "KasNetWallet");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "wallet.testnet10.json"), "T");
            Assert.True(WalletSettings.MigrateLegacyDirectory(legacy, nu));
            var st = LegacyData.Inspect(legacy, nu, migratedThisRun: true);
            Assert.True(st.LegacyHasWallets);
            Assert.Contains("OLD password", LegacyData.NoticeText(st));
            Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(st, userConfirmed: false));
            Assert.True(Directory.Exists(legacy));
            LegacyData.DeleteLegacy(st, userConfirmed: true);
            Assert.False(Directory.Exists(legacy));
            Assert.True(File.Exists(Path.Combine(nu, "wallet.testnet10.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void G1_DeleteRefused_WhenNotImported()
    {
        var root = Tmp();
        try
        {
            var legacy = Path.Combine(root, "o"); var nu = Path.Combine(root, "n");
            Directory.CreateDirectory(legacy); Directory.CreateDirectory(nu);
            File.WriteAllText(Path.Combine(legacy, "wallet.testnet10.json"), "T");
            Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(LegacyData.Inspect(legacy, nu, false), userConfirmed: true));
            Assert.True(File.Exists(Path.Combine(legacy, "wallet.testnet10.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---------- G5 ----------
    [Fact]
    public void G5_ReadOnlySourceFiles_CopiesAreWritable()
    {
        var root = Tmp();
        try
        {
            var legacy = Path.Combine(root, "o"); var nu = Path.Combine(root, "n");
            Directory.CreateDirectory(legacy);
            var f = Path.Combine(legacy, "unlock-state.testnet10.json");
            File.WriteAllText(f, "{}");
            if (OperatingSystem.IsWindows()) File.SetAttributes(f, FileAttributes.ReadOnly);
            else File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Assert.True(WalletSettings.MigrateLegacyDirectory(legacy, nu));
            var copy = Path.Combine(nu, "unlock-state.testnet10.json");
            Assert.Equal(0, (int)(File.GetAttributes(copy) & FileAttributes.ReadOnly));
            File.WriteAllText(copy, "{\"x\":1}");
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(copy) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---------- G3 ----------
    [Fact]
    public void G3_SmallDaaRegression_Tolerated_LargeBlocked()
    {
        var st = new NodeStatus("f", "testnet-10", true, true, 611_000_000);
        WalletManager.EnsureNodeFresh(st, WalletNetwork.Testnet10, 611_000_000 + WalletManager.DaaRegressionTolerance);
        Assert.Throws<TransferException>(() => WalletManager.EnsureNodeFresh(st, WalletNetwork.Testnet10, 611_000_000 + WalletManager.DaaRegressionTolerance + 1));
    }

    [Fact]
    public void G3_SwitchingNode_ResetsSeenDaa()
    {
        var root = Tmp();
        try
        {
            var m = new WalletManager(root, new WalletSettings { Network = WalletNetwork.Testnet10 }, KasNetWallet.Core.Keystore.KdfParameters.TestOnly);
            m.UseNode(new TransferTests.FakeNode("testnet-10"));
            typeof(WalletManager).GetField("_highestDaaSeen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(m, 999_000_000UL);
            Assert.Equal(999_000_000UL, m.HighestDaaSeen);
            m.UseNode(new TransferTests.FakeNode("testnet-10"));
            Assert.Equal(0UL, m.HighestDaaSeen);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---------- G2 / F3 ----------
    private static int Outcome(HdAccount acc, Coin[] coins, ulong amt, MassPolicy? mp)
    {
        try { var p = TransferService.Build(acc, coins, TestData.AbandonTestnet, amt, massPolicy: mp); return p.FoldedChangeSompi > 0 ? 0 : 2; }
        catch (ChangeBlockedException) { return 1; }
    }

    [Theory]
    [InlineData(false, 2_036UL, 9_000_000UL, 10_020_000UL, 5_000UL)]
    [InlineData(true, 203_600UL, 1_500_000UL, 2_010_000UL, 2_500UL)]
    public void G2_FoldBlockChange_IsMonotonic(bool post, ulong baseFee, ulong from, ulong to, ulong step)
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 10 * Kas1, 1) };
        var mp = new MassPolicy { ToccataActive = post };
        var last = -1;
        for (var change = from; change <= to; change += step)
        {
            var o = Outcome(acc, coins, 10 * Kas1 - baseFee - change, mp);
            Assert.True(o >= last, $"post={post}: non-monotonic at change={change}: {last}->{o}");
            last = o;
        }
        Assert.Equal(2, last);
    }

    [Fact]
    public void G2_Blocked_SuggestsWorkingLowerAmount_AndRemainderToPayment()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 10 * Kas1, 1) };
        var amt = 10 * Kas1 - 203_600 - 1_980_000;
        var ex = Assert.Throws<ChangeBlockedException>(() => TransferService.Build(acc, coins, TestData.AbandonTestnet, amt));
        Assert.NotNull(ex.SuggestedAmountSompi);
        Assert.True(ex.SuggestedAmountSompi < amt);
        Assert.Contains("LOWER the amount to " + Kas.Format(ex.SuggestedAmountSompi!.Value), ex.Message);
        Assert.Contains("Send max", ex.Message);
        Assert.Contains("own address", ex.Message);
        var ok = TransferService.Build(acc, coins, TestData.AbandonTestnet, ex.SuggestedAmountSompi.Value);
        Assert.Equal(0UL, ok.FoldedChangeSompi);
        Assert.True(ok.ChangeSompi >= 2_000_000);
        // "add the remainder to the payment": spend exactly the blocked inputs with no change
        var use = coins.Where(c => ex.Inputs.Contains(c.Outpoint)).ToArray();
        var absorb = TransferService.BuildSendMax(acc, use, TestData.AbandonTestnet);
        Assert.Equal(0UL, absorb.ChangeSompi);
        Assert.True(absorb.AmountSompi > amt);
        Assert.Equal(10 * Kas1, absorb.AmountSompi + absorb.FeeSompi);
    }
}
