using KasNetWallet.Core;
using Xunit;

namespace KasNetWallet.Core.Tests;

public class RenameMigrationTests
{
    private static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "kdw-mig-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Migrates_legacy_folder_bytewise_and_keeps_legacy()
    {
        var root = Tmp();
        try
        {
            var legacy = Path.Combine(root, WalletSettings.LegacyDirectoryName);
            var nu = Path.Combine(root, WalletSettings.DirectoryName);
            Directory.CreateDirectory(Path.Combine(legacy, "sub"));
            File.WriteAllText(Path.Combine(legacy, "wallet.testnet10.json"), "{\"v\":1}");
            File.WriteAllText(Path.Combine(legacy, "sub", "x.txt"), "y");
            Assert.True(WalletSettings.MigrateLegacyDirectory(legacy, nu));
            Assert.Equal("{\"v\":1}", File.ReadAllText(Path.Combine(nu, "wallet.testnet10.json")));
            Assert.True(File.Exists(Path.Combine(nu, "sub", "x.txt")));
            Assert.True(File.Exists(Path.Combine(legacy, "wallet.testnet10.json")));
            Assert.False(WalletSettings.MigrateLegacyDirectory(legacy, nu));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Does_not_touch_existing_new_folder()
    {
        var root = Tmp();
        try
        {
            var legacy = Path.Combine(root, "old"); var nu = Path.Combine(root, "new");
            Directory.CreateDirectory(legacy); Directory.CreateDirectory(nu);
            File.WriteAllText(Path.Combine(legacy, "a"), "1");
            Assert.False(WalletSettings.MigrateLegacyDirectory(legacy, nu));
            Assert.False(File.Exists(Path.Combine(nu, "a")));
            Assert.False(WalletSettings.MigrateLegacyDirectory(Path.Combine(root, "none"), Path.Combine(root, "n2")));
        }
        finally { Directory.Delete(root, true); }
    }
}
