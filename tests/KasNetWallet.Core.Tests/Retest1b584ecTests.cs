using KasNetWallet.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Round 9 (testcases/kasnetwallet-1b584ec-retest.md): N10 malformed settings never crash start-up paths,
/// N11 the message names the broken file, N12 case-insensitive Network detection.</summary>
public class Retest1b584ecTests
{
    private sealed class Dirs : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "kdw-r9-" + Guid.NewGuid().ToString("N"));
        public string Old => Path.Combine(Root, "KaspaDesktopWallet");
        public string New => Path.Combine(Root, "KasNetWallet");
        public string Tmp => New + ".migrating";
        public Dirs(bool createNew = true)
        {
            Directory.CreateDirectory(Old); W(Old, "wallet.testnet10.json", "W");
            if (createNew) { Directory.CreateDirectory(New); W(New, "wallet.testnet10.json", "W"); }
        }
        public void W(string dir, string f, string c) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, f), c); }
        public string R(string dir, string f) => File.ReadAllText(Path.Combine(dir, f));
        public bool E(string dir, string f) => File.Exists(Path.Combine(dir, f));
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    public static IEnumerable<object[]> Malformed() => new[]
    {
        new object[] { "{\"RpcTimeoutSeconds\":1,\"RpcTimeoutSeconds\":2}" },
        new object[] { "{\"Network\":1.5}" },
        new object[] { "{\"Network\":99999999999}" },
        new object[] { "{\"Network\":\"Mainnet\",\"Network\":\"Testnet10\"}" },
        new object[] { "{\"Network\":1e400}" },
    };

    // ---------- N10: "tx" (existing new folder) ----------
    [Theory, MemberData(nameof(Malformed))]
    public void N10_OldMalformed_InspectAndNotice_DoNotThrow_DeleteAborts(string bad)
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", bad);
        var st = LegacyData.Inspect(d.Old, d.New, false);
        _ = LegacyData.NoticeText(st);
        if (st.SettingsError is not null)
        {
            Assert.Contains("old", st.SettingsError);
            var ex = Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(st, true));
            Assert.True(d.E(d.Old, "settings.json"));
        }
        else LegacyData.DeleteLegacy(st, true); // values that load fine (e.g. Network 1.5 → reset by Normalize) merge without Network
    }

    [Fact]
    public void N10_DuplicateKey_IsReportedAsCorrupt()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\":1,\"RpcTimeoutSeconds\":2}");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.NotNull(st.SettingsError);
        Assert.Contains("duplicate", st.SettingsError);
    }

    [Theory, MemberData(nameof(Malformed))]
    public void N10_Load_TreatsMalformedAsDefaults_NeverThrows(string bad)
    {
        using var d = new Dirs();
        d.W(d.New, "settings.json", bad);
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"), out _);
        Assert.Equal(WalletNetwork.Testnet10, s.Network); Assert.False(s.MainnetEnabled);
    }

    // ---------- N10: "fr" (first run) ----------
    [Theory, MemberData(nameof(Malformed))]
    public void N10_FirstRun_Malformed_Migrates_NoLeftover_LegacyKept(string bad)
    {
        using var d = new Dirs(createNew: false);
        d.W(d.Old, "settings.json", bad);
        Assert.True(WalletSettings.MigrateLegacyDirectory(d.Old, d.New));
        Assert.False(Directory.Exists(d.Tmp));
        Assert.True(d.E(d.New, "wallet.testnet10.json"));
        Assert.Equal(bad, d.R(d.Old, "settings.json"));
        if (d.E(d.New, "settings.json"))
        {
            var j = JsonNode.Parse(d.R(d.New, "settings.json"))!.AsObject();
            Assert.DoesNotContain(j, kv => kv.Key.Equals("Network", StringComparison.OrdinalIgnoreCase));
        }
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"));
        Assert.Equal(WalletNetwork.Testnet10, s.Network);
        _ = LegacyData.NoticeText(LegacyData.Inspect(d.Old, d.New, true));
    }

    [Fact]
    public void N10_FirstRun_LeftoverMigratingFolder_IsReplaced()
    {
        using var d = new Dirs(createNew: false);
        d.W(d.Tmp, "junk.txt", "x");
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\":1,\"RpcTimeoutSeconds\":2}");
        Assert.True(WalletSettings.MigrateLegacyDirectory(d.Old, d.New));
        Assert.False(Directory.Exists(d.Tmp));
        Assert.False(d.E(d.New, "junk.txt"));
        Assert.True(d.E(d.New, "wallet.testnet10.json"));
    }

    [Fact]
    public void N10_FirstRun_CopyFailure_RemovesTempFolder()
    {
        using var d = new Dirs(createNew: false);
        d.W(d.Old, "locked.bin", "x");
        using (new FileStream(Path.Combine(d.Old, "locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => WalletSettings.MigrateLegacyDirectory(d.Old, d.New));
        Assert.False(Directory.Exists(d.Tmp));
        Assert.False(Directory.Exists(d.New));
        Assert.True(WalletSettings.MigrateLegacyDirectory(d.Old, d.New)); // next start succeeds
    }

    // ---------- N11 ----------
    [Fact] // v17/v20
    public void N11_TypeErrorInNewFile_NamesNewFile()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"AutoLockMinutes\": 9}");
        d.W(d.New, "settings.json", "{\"RpcTimeoutSeconds\": \"abc\"}");
        var ex = Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, false), true));
        Assert.Contains("the new settings.json", ex.Message);
        Assert.DoesNotContain("the old settings.json", ex.Message);
    }

    [Fact]
    public void N11_TypeErrorInOldFile_NamesOldFile()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\": \"abc\"}");
        d.W(d.New, "settings.json", "{\"AutoLockMinutes\": 9}");
        var ex = Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, false), true));
        Assert.Contains("the old settings.json", ex.Message);
    }

    [Fact]
    public void N11_CorruptNewFile_NamesNewFile()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{}");
        d.W(d.New, "settings.json", "{\"A\":1,\"A\":2}");
        Assert.Contains("new settings file", LegacyData.Inspect(d.Old, d.New, false).SettingsError);
    }

    // ---------- N12 ----------
    [Theory] // f2
    [InlineData("{\"NETWORK\": \"Mainnet\"}", true)]
    [InlineData("{\"network\": 0}", true)]
    [InlineData("{\"network\": \"testnet10\"}", false)]
    public void N12_NetworkKey_CaseInsensitive(string old, bool mainnet)
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", old);
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.Equal(mainnet, st.OldUsesMainnet);
        Assert.Equal(mainnet, LegacyData.NoticeText(st).Contains("used MAINNET"));
    }
}
