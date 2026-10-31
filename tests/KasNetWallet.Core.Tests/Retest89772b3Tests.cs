using KasNetWallet.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Round 7 (testcases/kasnetwallet-89772b3-retest.md): N4 broken/unreadable settings abort merge and delete,
/// N6 Network/MainnetEnabled never imported, N5 mainnet note wording.</summary>
public class Retest89772b3Tests
{
    private sealed class Dirs : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "kdw-r7-" + Guid.NewGuid().ToString("N"));
        public string Old => Path.Combine(Root, "KaspaDesktopWallet");
        public string New => Path.Combine(Root, "KasNetWallet");
        public Dirs()
        {
            Directory.CreateDirectory(Old); Directory.CreateDirectory(New);
            W(Old, "wallet.testnet10.json", "W"); W(New, "wallet.testnet10.json", "W"); // identical copy: delete path
        }
        public void W(string dir, string f, string c) => File.WriteAllText(Path.Combine(dir, f), c);
        public string R(string dir, string f) => File.ReadAllText(Path.Combine(dir, f));
        public bool E(string dir, string f) => File.Exists(Path.Combine(dir, f));
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    private static void AssertDeleteAborted(Dirs d, string? newBefore)
    {
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.NotNull(st.SettingsError);
        Assert.Contains("WARNING", LegacyData.NoticeText(st));
        var ex = Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(st, true));
        Assert.Contains("settings file", ex.Message);
        Assert.True(d.E(d.Old, "settings.json")); Assert.True(d.E(d.Old, "wallet.testnet10.json"));
        Assert.Equal(newBefore, d.E(d.New, "settings.json") ? d.R(d.New, "settings.json") : null);
    }

    [Fact] // m1c
    public void N4_OldSettingsCorrupt_DeleteAborted()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{ \"RpcTimeoutSeconds\": 42, ");
        AssertDeleteAborted(d, null);
    }

    [Fact] // m1c variant: valid JSON but not an object
    public void N4_OldSettingsNotObject_DeleteAborted()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "[1,2]");
        d.W(d.New, "settings.json", "{}");
        AssertDeleteAborted(d, "{}");
    }

    [Fact] // m1d: old unreadable, new present (exclusive lock; chmod 000 does not stop root)
    public void N4_OldSettingsUnreadable_NewPresent_DeleteAborted()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\": 42}");
        d.W(d.New, "settings.json", "{\"AutoLockMinutes\": 9}");
        using (new FileStream(Path.Combine(d.Old, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var st = LegacyData.Inspect(d.Old, d.New, false);
            Assert.Throws<InvalidOperationException>(() => LegacyData.DeleteLegacy(st, true));
        }
        Assert.True(d.E(d.Old, "settings.json"));
        Assert.Equal("{\"AutoLockMinutes\": 9}", d.R(d.New, "settings.json"));
    }

    [Fact] // m1f
    public void N4_NewSettingsCorrupt_DeleteAborted_NewFileUntouched()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\": 42}");
        d.W(d.New, "settings.json", "{broken");
        AssertDeleteAborted(d, "{broken");
    }

    [Fact]
    public void N4_Import_WithCorruptSettings_CopiesNothing()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.mainnet.json", "M2");
        d.W(d.Old, "settings.json", "not json");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.True(st.HasImportable);
        Assert.Throws<InvalidOperationException>(() => LegacyData.Import(st));
        Assert.False(d.E(d.New, "wallet.mainnet.json"));
    }

    [Fact]
    public void N4_HealthySettings_NoWarning()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"RpcTimeoutSeconds\": 42}");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.Null(st.SettingsError);
        Assert.DoesNotContain("WARNING", LegacyData.NoticeText(st));
    }

    [Fact] // mn7
    public void N6_DeletePath_OldMainnetEnabled_DoesNotSwitchNetwork_AndIsAnnounced()
    {
        using var d = new Dirs();
        d.W(d.Old, "settings.json", "{\"Network\": \"Mainnet\", \"MainnetEnabled\": true, \"RpcTimeoutSeconds\": 42}");
        var st = LegacyData.Inspect(d.Old, d.New, false);
        Assert.True(st.ShouldOfferDelete);
        var text = LegacyData.NoticeText(st);
        Assert.Contains("used MAINNET", text); Assert.Contains("not imported", text);
        LegacyData.DeleteLegacy(st, true);
        var j = JsonNode.Parse(d.R(d.New, "settings.json"))!.AsObject();
        Assert.False(j.ContainsKey("Network")); Assert.False(j.ContainsKey("MainnetEnabled"));
        Assert.Equal(42, j["RpcTimeoutSeconds"]!.GetValue<int>());
        var s = WalletSettings.Load(Path.Combine(d.New, "settings.json"));
        Assert.Equal(WalletNetwork.Mainnet, s.Network); Assert.True(s.MainnetEnabled);
    }

    [Fact]
    public void N6_ImportPath_NetworkKeysNotImported()
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.mainnet.json", "M2");
        d.W(d.Old, "settings.json", "{\"Network\": 0, \"MainnetEnabled\": true, \"AutoLockMinutes\": 20}");
        d.W(d.New, "settings.json", "{\"RpcTimeoutSeconds\": 15}");
        LegacyData.Import(LegacyData.Inspect(d.Old, d.New, false));
        var j = JsonNode.Parse(d.R(d.New, "settings.json"))!.AsObject();
        Assert.False(j.ContainsKey("Network")); Assert.False(j.ContainsKey("MainnetEnabled"));
        Assert.Equal(20, j["AutoLockMinutes"]!.GetValue<int>()); Assert.Equal(15, j["RpcTimeoutSeconds"]!.GetValue<int>());
    }

    [Theory] // mn5 + N5: the note names only the new file's saved network, never an old MainnetEnabled value
    [InlineData("{\"MainnetEnabled\": false}", "the default network, mainnet")]
    [InlineData("{\"Network\": \"Testnet10\"}", "saved in the new settings (Testnet10)")]
    [InlineData("{\"Network\": \"Mainnet\", \"MainnetEnabled\": true}", "saved in the new settings (Mainnet)")]
    public void N5_MainnetNote_DescribesNewSettings(string newSettings, string expect)
    {
        using var d = new Dirs();
        d.W(d.Old, "wallet.mainnet.json", "M2");
        d.W(d.Old, "settings.json", "{\"Network\": \"Mainnet\", \"MainnetEnabled\": true}");
        d.W(d.New, "settings.json", newSettings);
        var text = LegacyData.NoticeText(LegacyData.Inspect(d.Old, d.New, false));
        Assert.Contains(expect, text);
        Assert.DoesNotContain("MainnetEnabled =", text);
    }
}
