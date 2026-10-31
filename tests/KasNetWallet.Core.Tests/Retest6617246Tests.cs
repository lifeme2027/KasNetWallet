using KasNetWallet.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Round 6 (testcases/kasnetwallet-6617246-retest.md): M1 settings merged before the old folder is deleted,
/// M2 mainnet notice matches MainnetEnabled.</summary>
public class Retest6617246Tests
{
    private sealed class Dirs : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "kdw-r6-" + Guid.NewGuid().ToString("N"));
        public string Old => Path.Combine(Root, "KaspaDesktopWallet");
        public string New => Path.Combine(Root, "KasNetWallet");
        public Dirs() { Directory.CreateDirectory(Old); Directory.CreateDirectory(New); }
        public void W(string dir, string f, string c) => File.WriteAllText(Path.Combine(dir, f), c);
        public string R(string dir, string f) => File.ReadAllText(Path.Combine(dir, f));
        public bool E(string dir, string f) => File.Exists(Path.Combine(dir, f));
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    // ---------- M1 ----------
    [Fact]
    public void M1_NoImportable_NewHasNoSettings_DeleteCopiesSettingsFirst()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "W"); d.W(d.New, "wallet.testnet10.json", "W");
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\": 42}");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.False(st.HasImportable); Assert.True(st.ShouldOfferDelete);
        Assert.Contains("merged", LegacyData.NoticeText(st));
        LegacyData.DeleteLegacy(st, true);
        Assert.False(Directory.Exists(d.Old));
        Assert.Equal(42, JsonNode.Parse(d.R(d.New, "settings.json"))!["RpcTimeoutSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void M1_NoImportable_NewHasSettings_DeleteMergesAbsentKeysOnly()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "W"); d.W(d.New, "wallet.testnet10.json", "W");
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\": 42, \"AutoLockMinutes\": 20}");
        d.W(d.New, "settings.json", "{\"RpcTimeoutSeconds\": 15}");
        LegacyData.DeleteLegacy(LegacyData.Inspect(d.Old, d.New, false), true);
        var j = JsonNode.Parse(d.R(d.New, "settings.json"))!;
        Assert.Equal(15, j["RpcTimeoutSeconds"]!.GetValue<int>());
        Assert.Equal(20, j["AutoLockMinutes"]!.GetValue<int>());
        Assert.False(d.E(d.Old, "settings.json"));
    }

    [Fact]
    public void M1_NoOldSettings_DeleteStillWorks_AndNoMergeLine()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.testnet10.json", "W"); d.W(d.New, "wallet.testnet10.json", "W");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.DoesNotContain("merged", LegacyData.NoticeText(st));
        LegacyData.DeleteLegacy(st, true);
        Assert.False(d.E(d.New, "settings.json"));
        Assert.False(Directory.Exists(d.Old));
    }

    // ---------- M2 (superseded by N6 in round 7: Network/MainnetEnabled are not imported) ----------
    [Theory]
    [InlineData("true", null)]
    [InlineData("false", null)]
    [InlineData(null, null)]
    [InlineData("true", "{\"MainnetEnabled\": false}")]
    [InlineData("false", "{\"MainnetEnabled\": true}")]
    public void M2_MainnetNote_MatchesSettingsAfterImport(string? oldEnabled, string? newSettings)
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.mainnet.json", "M");
        d.W(d.Old, "settings.json", oldEnabled is null ? "{\"Network\": \"Mainnet\"}" : $"{{\"Network\": \"Mainnet\", \"MainnetEnabled\": {oldEnabled}}}");
        if (newSettings is not null) d.W(d.New, "settings.json", newSettings);
        var st = LegacyData.Inspect(d.Old, d.New, false);
        var text = LegacyData.NoticeText(st);
        Assert.Contains("not imported", text);
        Assert.Contains("mainnet", text);
        Assert.DoesNotContain("will start on MAINNET", text);
        Assert.DoesNotContain("MainnetEnabled = false", text); // N5: nothing attributed to the old file it does not say

        LegacyData.MergeSettings(d.Old, d.New);
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"));
        Assert.Equal(WalletNetwork.Mainnet, s.Network);
        Assert.Equal(newSettings?.Contains("false") != true, s.MainnetEnabled); // only the new file's own value (D33 default true)
    }
}
