using KasNetWallet.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Round 8 (testcases/kasnetwallet-806e349-retest.md): N7 type check of the merged settings, N8 case-insensitive keys,
/// N9 first-run migration follows D27.</summary>
public class Retest806e349Tests
{
    private sealed class Dirs : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "kdw-r8-" + Guid.NewGuid().ToString("N"));
        public string Old => Path.Combine(Root, "KaspaDesktopWallet");
        public string New => Path.Combine(Root, "KasNetWallet");
        public Dirs(bool createNew = true)
        {
            Directory.CreateDirectory(Old); W(Old, "wallet.testnet10.json", "W");
            if (createNew) { Directory.CreateDirectory(New); W(New, "wallet.testnet10.json", "W"); }
        }
        public void W(string dir, string f, string c) => File.WriteAllText(Path.Combine(dir, f), c);
        public string R(string dir, string f) => File.ReadAllText(Path.Combine(dir, f));
        public bool E(string dir, string f) => File.Exists(Path.Combine(dir, f));
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    // ---------- N7 ----------
    [Theory] // t1, t1b and more type errors
    [InlineData("{\"RpcTimeoutSeconds\": \"abc\"}", null)]
    [InlineData("{\"RpcTimeoutSeconds\": \"abc\"}", "{\"Network\": \"Mainnet\", \"MainnetEnabled\": true, \"AutoLockMinutes\": 9}")]
    [InlineData("{\"MassPolicies\": \"x\"}", "{\"AutoLockMinutes\": 9}")]
    [InlineData("{\"FeePolicy\": 5}", null)]
    [InlineData("{\"AutoLockMinutes\": 1.5}", null)]
    public void N7_WrongValueType_AbortsDelete_NewSettingsUntouched(string oldSettings, string? newSettings)
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", oldSettings);
        if (newSettings is not null) d.W(d.New, "settings.json", newSettings);
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.NotNull(st.SettingsError);
        Assert.Contains("WARNING", LegacyData.NoticeText(st));
        var ex = Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(st, true));
        Assert.Contains("would not load", ex.Message);
        Assert.True(d.E(d.Old, "settings.json"));
        Assert.Equal(newSettings, d.E(d.New, "settings.json") ? d.R(d.New, "settings.json") : null);
        if (newSettings?.Contains("Mainnet") == true)
        {   // t1b: the user's mainnet choice in the new settings is kept
            var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"));
            Assert.Equal(WalletNetwork.Mainnet, s.Network); Assert.True(s.MainnetEnabled);
        }
    }

    [Fact]
    public void N7_WrongValueType_AbortsImport_CopiesNothing()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.mainnet.json", "M");
        d.W(d.Old, "settings.json", "{\"MassPolicies\": \"x\"}");
        Assert.Throws<InvalidOperationException>(() => LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false)));
        Assert.False(d.E(d.New, "wallet.mainnet.json"));
    }

    [Fact]
    public void N7_ValidTypes_MergeAndLoad()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\": 42, \"FeePolicy\": {\"WarnPercent\": 5}, \"Unknown\": \"kept\"}");
        LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, false), true);
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"), out var notes);
        Assert.Empty(notes);
        Assert.Equal(42, s.RpcTimeoutSeconds); Assert.Equal(5, s.FeePolicy.WarnPercent);
    }

    // ---------- N8 ----------
    [Fact] // t6
    public void N8_CaseVariantKey_IsNotDuplicated()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"rpcTimeoutSeconds\": 42, \"autolockminutes\": 20, \"ClipboardClearSeconds\": 30}");
        d.W(d.New, "settings.json", "{\"RpcTimeoutSeconds\": 15, \"AutoLockMinutes\": 9}");
        LegacyData.MergeSettings(d.Old, d.New);
        var text = d.R(d.New, "settings.json");
        var j = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(3, j.Count);
        Assert.DoesNotContain("rpcTimeoutSeconds", text); Assert.DoesNotContain("autolockminutes", text);
        Assert.Equal(15, j["RpcTimeoutSeconds"]!.GetValue<int>());
        Assert.Equal(30, j["ClipboardClearSeconds"]!.GetValue<int>());
    }

    // ---------- N9 ----------
    [Fact]
    public void N9_FirstRun_DropsNetworkAndMainnetEnabled_KeepsOthers()
    {
        using var d = new Dirs(createNew: false);
        d.W(d.Old, "settings.json", "{\"Network\": \"Mainnet\", \"mainnetEnabled\": true, \"RpcTimeoutSeconds\": 42}");
        Assert.True(WalletSettings.MigrateLegacyDirectory(d.Old, d.New));
        var j = JsonNode.Parse(d.R(d.New, "settings.json"))!.AsObject();
        Assert.DoesNotContain(j, kv => kv.Key.Equals("Network", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("MainnetEnabled", StringComparison.OrdinalIgnoreCase));
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"));
        Assert.Equal(WalletNetwork.Testnet10, s.Network); Assert.False(s.MainnetEnabled); Assert.Equal(42, s.RpcTimeoutSeconds);
        Assert.Equal("W", d.R(d.New, "wallet.testnet10.json"));
        Assert.Contains("Mainnet", d.R(d.Old, "settings.json")); // legacy copy untouched
        Assert.True(LegacyData.Inspect(d.Old, d.New, true).OldUsesMainnet);
    }

    [Theory]
    [InlineData("{\"Network\": \"Mainnet\", \"MainnetEnabled\": true, \"RpcTimeoutSeconds\": \"abc\"}")]
    [InlineData("{broken")]
    public void N9_FirstRun_BadSettings_NotCarriedOver_LegacyKept_AndReported(string bad)
    {
        using var d = new Dirs(createNew: false);
        d.W(d.Old, "settings.json", bad);
        Assert.True(WalletSettings.MigrateLegacyDirectory(d.Old, d.New));
        Assert.False(d.E(d.New, "settings.json"));
        Assert.True(d.E(d.New, "wallet.testnet10.json"));
        Assert.Equal(bad, d.R(d.Old, "settings.json"));
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"));
        Assert.Equal(WalletNetwork.Testnet10, s.Network); Assert.False(s.MainnetEnabled);
        var st = LegacyData.Inspect(d.Old, d.New, true);
        Assert.NotNull(st.SettingsError);
        Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(st, true));
    }
}
