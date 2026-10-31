using KasNetWallet.Core;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Round 10 (testcases/kasnetwallet-506a924-retest.md): N13 legacy fallback is forced to testnet-10 and read-only,
/// N14 damaged legacy-reminder.json is treated as absent, N15 notices show the raw Network value.</summary>
public class Retest506a924Tests
{
    private sealed class Dirs : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "kdw-r10-" + Guid.NewGuid().ToString("N"));
        public string Old => Path.Combine(Root, "KaspaDesktopWallet");
        public string New => Path.Combine(Root, "KasNetWallet");
        public Dirs() { Directory.CreateDirectory(Old); W(Old, "wallet.testnet10.json", "W"); }
        public void W(string dir, string f, string c) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, f), c); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
    private const string OldMain = "{\"Network\":\"Mainnet\",\"MainnetEnabled\":true,\"AutoLockMinutes\":7}";

    // ---------- N13 ----------
    [Fact]
    public void N13_MigrationFails_FallsBackToLegacy_ForcedTestnet_OldFileUntouched()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", OldMain);
        d.W(d.Old, "locked.bin", "x");
        string dir; string? reason;
        using (new FileStream(Path.Combine(d.Old, "locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            dir = WalletSettings.ResolveDirectory(d.Root, out reason);
        Assert.Equal(d.Old, dir);
        Assert.NotNull(reason);
        var path = Path.Combine(dir, "settings.json");
        var s = WalletSettings.Load(path);
        Assert.Equal(WalletNetwork.Mainnet, s.Network); // what the old file says…
        var note = s.ApplyStartupSafety(reason);
        Assert.NotNull(note); Assert.Contains("testnet-10", note); Assert.Contains("confirm", note);
        Assert.Equal(WalletNetwork.Testnet10, s.Network); Assert.False(s.MainnetEnabled); // …is not used
        Assert.Equal(7, s.AutoLockMinutes);
        s.Network = WalletNetwork.Mainnet; s.MainnetEnabled = true;
        s.Save(path);
        Assert.Equal(OldMain, File.ReadAllText(path)); // never rewritten
        Assert.False(Directory.Exists(d.New + ".migrating"));
    }

    [Fact]
    public void N13_NoFallback_NoChange()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", OldMain);
        var dir = WalletSettings.ResolveDirectory(d.Root, out var reason);
        Assert.Equal(d.New, dir); Assert.Null(reason);
        var s = WalletSettings.Load(Path.Combine(dir, "settings.json"));
        Assert.Null(s.ApplyStartupSafety(null));
        Assert.False(s.ReadOnlySession);
        s.Save(Path.Combine(dir, "settings.json"));
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void N13_UnreadableOldSettings_Fallback_StillTestnet()
    {
        var s = WalletSettings.Load(Path.Combine(Path.GetTempPath(), "kdw-r10-missing", "settings.json"));
        s.Network = WalletNetwork.Mainnet; s.MainnetEnabled = true;
        Assert.NotNull(s.ApplyStartupSafety("IOException: denied"));
        Assert.Equal(WalletNetwork.Testnet10, s.Network); Assert.False(s.MainnetEnabled);
    }

    // ---------- N14 ----------
    [Theory]
    [InlineData("{\"declinedDeletes\":1,\"declinedDeletes\":2}")]
    [InlineData("{\"declinedDeletes\":1.5}")]
    [InlineData("{\"declinedDeletes\":\"x\"}")]
    [InlineData("[1]")]
    [InlineData("not json")]
    public void N14_DamagedReminder_TreatedAsAbsent_PromptStillWorks(string bad)
    {
        using var d = new Dirs();
        d.W(d.New, "wallet.testnet10.json", "W");
        d.W(d.New, LegacyData.ReminderFile, bad);
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.Equal(0, st.DeclinedDeletes);
        Assert.True(st.ShouldOfferDelete);
        LegacyData.RecordDeclined(st); // overwrites the damaged file with a valid one
        Assert.Equal(1, LegacyData.Inspect(d.Old, d.New, false).DeclinedDeletes);
    }

    // ---------- N15 ----------
    [Theory]
    [InlineData("{\"Network\":1.5}", "Network=1.5 ")]
    [InlineData("{\"Network\":99999999999}", "Network=99999999999 ")]
    [InlineData("{\"Network\":\"foo\"}", "Network=\"foo\" ")]
    [InlineData("{\"Network\":7}", "Network=7 ")]
    [InlineData("{\"Network\":true}", "Network=true ")]
    public void N15_Notice_ShowsRawValue(string json, string expected)
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", json);
        var s = WalletSettings.Load(Path.Combine(d.Old, "settings.json"), out var notes);
        Assert.Equal(WalletNetwork.Testnet10, s.Network);
        Assert.Contains(notes, n => n.StartsWith(expected));
        Assert.DoesNotContain(notes, n => n.StartsWith("Network=-"));
    }
}
