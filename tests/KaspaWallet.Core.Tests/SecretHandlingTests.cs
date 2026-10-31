using System.Text.Json;
using KaspaWallet.Core;
using KaspaWallet.Core.Keystore;

namespace KaspaWallet.Core.Tests;

public class ImportRejectionTests
{
    // Valid 12-word vector with ONE word changed (last word about→above): checksum invalid.
    private const string OneWordChanged = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon above";

    [Fact]
    public void OneWordChanged_HardRejected_WithChecksumError()
    {
        var ex = Assert.Throws<FormatException>(() => MnemonicService.Normalize(OneWordChanged));
        Assert.Contains("checksum", ex.Message);
        Assert.DoesNotContain("abandon", ex.Message);
        Assert.Throws<FormatException>(() => HdAccount.FromMnemonic(OneWordChanged, WalletNetwork.Testnet10));
    }

    [Fact]
    public void OneWordChanged_ImportCreatesNoWalletFile()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var m = new WalletManager(dir, new WalletSettings(), KdfParameters.TestOnly);
            Assert.Throws<FormatException>(() => m.CreateOrImport(OneWordChanged, "password-123"));
            Assert.False(m.KeystoreExists);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(11)] [InlineData(13)] [InlineData(23)] [InlineData(25)]
    public void WrongWordCount_Rejected(int n)
    {
        var ex = Assert.Throws<FormatException>(() => MnemonicService.Normalize(string.Join(' ', Enumerable.Repeat("abandon", n))));
        Assert.Contains($"got {n}", ex.Message);
    }

    [Fact]
    public void UnknownWord_Rejected_WithPosition()
    {
        var ex = Assert.Throws<FormatException>(() => MnemonicService.Normalize(TestData.AbandonMnemonic.Replace("about", "bitcoinx")));
        Assert.Contains("Word #12", ex.Message);
    }

    [Fact]
    public void NoBreakSpace_And_FullWidth_Normalized() =>
        Assert.Equal(TestData.AbandonMnemonic, MnemonicService.Normalize(TestData.AbandonMnemonic.Replace(" ", "\u00a0").Replace("about", "ａｂｏｕｔ")));
}

public class SecretWrapperTests
{
    [Fact]
    public void SecretText_ToString_And_Interpolation_Redacted()
    {
        var s = new SecretText(TestData.AbandonMnemonic);
        Assert.Equal("[REDACTED]", s.ToString());
        Assert.Equal("m=[REDACTED]", $"m={s}");
        Assert.Equal(TestData.AbandonMnemonic, s.Reveal());
    }

    [Fact]
    public void SecretText_JsonSerialization_Refused()
    {
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new SecretText("x")));
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new { Phrase = new SecretText(TestData.AbandonMnemonic) }));
    }

    [Fact]
    public void HdAccount_ToString_Redacted_And_JsonRefused()
    {
        using var acc = HdAccount.FromMnemonic(new SecretText(TestData.AbandonMnemonic), WalletNetwork.Testnet10);
        var text = acc.ToString();
        Assert.Contains("[REDACTED]", text);
        Assert.DoesNotContain("prv", text);
        Assert.DoesNotContain("abandon", text);
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(acc));
    }

    [Fact]
    public void Core_NeverCallsToStringOnSecretNKaspaTypes()
    {
        // Static guard: Core source must not stringify ExtKey/Mnemonic/Key objects or serialize them.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/KaspaWallet.Core"));
        foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains("/obj/") && !p.Contains("/bin/")))
        {
            var src = File.ReadAllText(f);
            Assert.DoesNotMatch(@"(master|extKey|ExtKey|_key|Key)\s*\.ToString\(", src);
            Assert.DoesNotMatch(@"Mnemonic\([^;]*\)\.ToString\(", src);
            Assert.DoesNotMatch(@"JsonSerializer\.Serialize\([^)]*(Mnemonic|ExtKey|Key|HdAccount)", src);
        }
    }
}
