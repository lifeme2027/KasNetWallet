using System.Text;
using KaspaWallet.Core;
using KaspaWallet.Core.Keystore;

namespace KaspaWallet.Core.Tests;

public class KeystoreTests
{
    private const string Pw = "correct horse battery";

    [Fact]
    public void RoundTrip_ReturnsSameMnemonic()
    {
        var ks = WalletKeystore.Encrypt(TestData.AbandonMnemonic, Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly);
        Assert.Equal(TestData.AbandonMnemonic, WalletKeystore.Decrypt(ks, Pw));
        Assert.Equal(TestData.AbandonMainnet, ks.Address);
    }

    [Fact]
    public void WrongPassword_Throws()
    {
        var ks = WalletKeystore.Encrypt(TestData.AbandonMnemonic, Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly);
        Assert.Throws<WrongPasswordException>(() => WalletKeystore.Decrypt(ks, "wrong password"));
    }

    [Fact]
    public void FileContainsNoPlaintextSecret()
    {
        var dir = Directory.CreateTempSubdirectory("kdw-test").FullName;
        try
        {
            var path = Path.Combine(dir, "w.json");
            WalletKeystore.Save(WalletKeystore.Encrypt(TestData.AbandonMnemonic, Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly), path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("abandon", text);
            Assert.DoesNotContain(Pw, text);
            var loaded = WalletKeystore.Load(path);
            using var acc = WalletKeystore.Unlock(loaded, Pw);
            Assert.Equal(TestData.AbandonMainnet, acc.Address);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SaltAndNonce_AreRandomPerEncryption()
    {
        var a = WalletKeystore.Encrypt(TestData.AbandonMnemonic, Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly);
        var b = WalletKeystore.Encrypt(TestData.AbandonMnemonic, Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly);
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
    }

    [Fact]
    public void TamperedCiphertext_Fails()
    {
        var ks = WalletKeystore.Encrypt(TestData.AbandonMnemonic, Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly);
        var c = Convert.FromBase64String(ks.Ciphertext);
        c[0] ^= 1;
        ks.Ciphertext = Convert.ToBase64String(c);
        Assert.Throws<WrongPasswordException>(() => WalletKeystore.Decrypt(ks, Pw));
    }

    [Fact]
    public void TamperedAssociatedData_NetworkSwap_Fails()
    {
        var ks = WalletKeystore.Encrypt(TestData.AbandonMnemonic, Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly);
        ks.Network = WalletNetwork.Testnet10.ToString();
        Assert.Throws<WrongPasswordException>(() => WalletKeystore.Decrypt(ks, Pw));
    }

    [Fact]
    public void WeakKdfParameters_Rejected()
    {
        var ks = WalletKeystore.Encrypt(TestData.AbandonMnemonic, Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly);
        ks.Argon2MemoryKiB = 8;
        Assert.Throws<InvalidDataException>(() => WalletKeystore.Decrypt(ks, Pw));
    }

    [Fact]
    public void InvalidMnemonic_NotStored() =>
        Assert.Throws<FormatException>(() => WalletKeystore.Encrypt("abandon abandon", Pw, WalletNetwork.Mainnet, KdfParameters.TestOnly));

    [Fact]
    public void WalletManager_CreateUnlockLock_AndNoOverwrite()
    {
        var dir = Directory.CreateTempSubdirectory("kdw-test").FullName;
        try
        {
            var mgr = new WalletManager(dir, new WalletSettings { Network = WalletNetwork.Testnet10 }, KdfParameters.TestOnly);
            Assert.Throws<ArgumentException>(() => mgr.CreateOrImport(TestData.AbandonMnemonic, "short"));
            mgr.CreateOrImport(TestData.AbandonMnemonic, Pw);
            Assert.Throws<InvalidOperationException>(() => mgr.CreateOrImport(TestData.AbandonMnemonic, Pw));
            Assert.Throws<WrongPasswordException>(() => mgr.Unlock("nope nope nope"));
            Assert.False(mgr.IsUnlocked);
            mgr.Unlock(Pw);
            Assert.True(mgr.IsUnlocked);
            Assert.Equal(TestData.AbandonTestnet, mgr.Address);
            mgr.Lock();
            Assert.False(mgr.IsUnlocked);
            Assert.Equal(TestData.AbandonTestnet, mgr.Address); // public address remains readable while locked
        }
        finally { Directory.Delete(dir, true); }
    }
}
