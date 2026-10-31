using KasNetWallet.Core;
using KasNetWallet.Core.Transfer;
using System.Text.Json.Nodes;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Round 4 (testcases/kasnetwallet-570d403-retest.md): N1 selective delete, N3 history on renamed import, N2 monotonic
/// pre-Toccata order with suggestions, settings merge of absent keys only, mainnet switch notice.</summary>
public class Retest570d403Tests
{
    private sealed class Dirs : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "kdw-r4-" + Guid.NewGuid().ToString("N"));
        public string Old => Path.Combine(Root, "KaspaDesktopWallet");
        public string New => Path.Combine(Root, "KasNetWallet");
        public Dirs() { Directory.CreateDirectory(Old); Directory.CreateDirectory(New); }
        public void W(string dir, string f, string c) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f))!); File.WriteAllText(Path.Combine(dir, f), c); }
        public string R(string dir, string f) => File.ReadAllText(Path.Combine(dir, f));
        public bool E(string dir, string f) => File.Exists(Path.Combine(dir, f));
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    // ---------- N1 ----------
    [Fact]
    public void N1_Delete_IsSelective_KeepsBackupBakAndUnknown_AndListsThem()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); d.W(d.Old, "history.testnet10.json", "H");
        d.W(d.Old, "unlock-state.testnet10.json", "U"); d.W(d.Old, "settings.json", "{}");
        d.W(d.Old, Path.Combine("backup", "wallet.testnet10.json"), "OLD-BACKUP");
        d.W(d.Old, "wallet.testnet10.json.bak", "BAK"); d.W(d.Old, "notes.txt", "n");
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        var st = LegacyData.Inspect(d.Old, d.New, true);
        var plan = LegacyData.PlanDelete(st);
        Assert.Equal(new[] { "backup" + Path.DirectorySeparatorChar, "notes.txt", "wallet.testnet10.json.bak" }, plan.Keep);
        Assert.Contains("backup", LegacyData.NoticeText(st));
        Assert.Contains("wallet.testnet10.json.bak", LegacyData.NoticeText(st));
        var done = LegacyData.DeleteLegacy(st, userConfirmed: true);
        Assert.Equal(plan.Keep, done.Keep);
        Assert.False(d.E(d.Old, "wallet.testnet10.json"));
        Assert.False(d.E(d.Old, "history.testnet10.json"));
        Assert.False(d.E(d.Old, "unlock-state.testnet10.json"));
        Assert.False(d.E(d.Old, "settings.json"));
        Assert.Equal("OLD-BACKUP", d.R(d.Old, Path.Combine("backup", "wallet.testnet10.json")));
        Assert.Equal("BAK", d.R(d.Old, "wallet.testnet10.json.bak"));
        Assert.True(d.E(d.Old, "notes.txt"));
        Assert.Contains("backup", LegacyData.KeptText(done.Keep));
    }

    [Fact]
    public void N1_DivergedHistory_IsKept()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); d.W(d.Old, "history.testnet10.json", "H-old");
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        d.W(d.New, "history.testnet10.json", "H-new"); // new history grew → old one is not a verified copy
        var done = LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, true), true);
        Assert.Contains("history.testnet10.json", done.Keep);
        Assert.Equal("H-old", d.R(d.Old, "history.testnet10.json"));
        Assert.False(d.E(d.Old, "wallet.testnet10.json"));
    }

    [Fact]
    public void N1_OnlyVerifiedFiles_FolderRemoved_NoRecursiveDelete()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); d.W(d.Old, "history.testnet10.json", "H");
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        var done = LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, true), true);
        Assert.Empty(done.Keep);
        Assert.False(Directory.Exists(d.Old));
        Assert.Equal("", LegacyData.KeptText(done.Keep));
    }

    [Fact]
    public void N1_EmptySubfolder_IsKept()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); Directory.CreateDirectory(Path.Combine(d.Old, "backup"));
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        var done = LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, true), true);
        Assert.True(Directory.Exists(Path.Combine(d.Old, "backup")));
        Assert.Single(done.Keep);
    }

    // ---------- N3 ----------
    [Fact]
    public void N3_RenamedImport_BringsHistoryUnderNewName()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "A"); d.W(d.Old, "history.testnet10.json", "H-A"); d.W(d.Old, "unlock-state.testnet10.json", "U");
        d.W(d.New, "wallet.testnet10.json", "B"); d.W(d.New, "history.testnet10.json", "H-B");
        var map = LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        var renamed = map["wallet.testnet10.json"];
        var net = renamed["wallet.".Length..^".json".Length];
        Assert.Equal("H-A", d.R(d.New, $"history.{net}.json"));
        Assert.Equal("H-B", d.R(d.New, "history.testnet10.json"));
        Assert.False(d.E(d.New, $"unlock-state.{net}.json"));
        Assert.Contains($"history.{net}.json", LegacyData.ConflictHint(map));
        // the delete check now finds both the wallet and its history
        var done = LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, true), true);
        Assert.Empty(done.Keep);
    }

    // ---------- settings merge ----------
    [Fact]
    public void Settings_ExplicitDefaultInNew_IsNotOverwritten_AbsentKeyIsTaken()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\": 60, \"AutoLockMinutes\": 20}");
        d.W(d.New, "settings.json", "{\"RpcTimeoutSeconds\": 15}"); // user set it back to the default
        LegacyData.MergeSettings(d.Old, d.New);
        var j = JsonNode.Parse(d.R(d.New, "settings.json"))!;
        Assert.Equal(15, j["RpcTimeoutSeconds"]!.GetValue<int>());
        Assert.Equal(20, j["AutoLockMinutes"]!.GetValue<int>());
    }

    // ---------- mainnet notice (round 7 / N6: Network is never imported; the note only reports the old choice) ----------
    [Theory]
    [InlineData("\"Mainnet\"", null, true)]
    [InlineData("\"mainnet\"", "{}", true)]
    [InlineData("0", null, true)]
    [InlineData("\"Mainnet\"", "{\"Network\": \"Testnet10\"}", true)]
    [InlineData("\"Testnet10\"", null, false)]
    public void Mainnet_OldChoice_IsAnnounced_NotImported(string oldNet, string? newSettings, bool expect)
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.mainnet.json", "M");
        d.W(d.Old, "settings.json", $"{{\"Network\": {oldNet}, \"MainnetEnabled\": true}}");
        if (newSettings is not null) d.W(d.New, "settings.json", newSettings);
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.Equal(expect, st.OldUsesMainnet);
        Assert.Equal(expect, LegacyData.NoticeText(st).Contains("MAINNET"));
        Assert.DoesNotContain("will start on MAINNET", LegacyData.NoticeText(st));
    }

    // ---------- N2 ----------
    public static IEnumerable<object[]> N2Cases() =>
        from bal in new[] { 50_000_000UL, 100_000_000UL, 1_000_000_000UL }
        from rate in new[] { 500.0, 1000.0 }
        select new object[] { bal, rate };

    [Theory]
    [MemberData(nameof(N2Cases))]
    public void N2_PreToccata_HighRate_OrderIsMonotonic_AndEveryBlockHasSuggestion(ulong bal, double rate)
    {
        using var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
        var coins = new[] { TestData.FakeCoin(acc.Address, bal, 1) };
        var order = new List<string>();
        for (ulong rem = 3_000_000; rem <= 16_000_000; rem += 50_000)
        {
            string r;
            try
            {
                var p = TransferService.Build(acc, coins, TestData.AbandonTestnet, bal - rem, FeeMode.NodeEstimate, rate, massPolicy: new MassPolicy { ToccataActive = false });
                r = p.FoldedChangeSompi > 0 ? "FOLD" : "OK";
                if (r == "FOLD") Assert.True(rem < 9_500_000, $"fold at remainder {rem}");
            }
            catch (ChangeBlockedException e)
            {
                r = "BLOCK";
                Assert.True(e.SuggestedAmountSompi is not null, $"no suggestion at remainder {rem}");
                var s = TransferService.Build(acc, coins, TestData.AbandonTestnet, e.SuggestedAmountSompi!.Value, FeeMode.NodeEstimate, rate, massPolicy: new MassPolicy { ToccataActive = false });
                Assert.True(s.ChangeSompi > 0 && s.FoldedChangeSompi == 0);
            }
            if (order.Count == 0 || order[^1] != r) order.Add(r);
        }
        Assert.Equal(new[] { "FOLD", "BLOCK", "OK" }, order);
    }
}
