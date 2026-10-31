using System.Text.Json;
using KaspaWallet.Core.Keystore;
using KaspaWallet.Core.Transfer;
using NKaspa;
using Xunit;

namespace KaspaWallet.Core.Tests;

/// <summary>Regression tests for the MVP-1 tester report (KDW-04/05/08/09/10/11, file mode).</summary>
public class MvpReportFixTests
{
    private const string Pw = "correct horse 1";

    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "kdw-t-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static WalletManager Mgr(string dir) => new(dir, new WalletSettings { Network = WalletNetwork.Testnet10 }, KdfParameters.TestOnly);
    private static string[] Words => TestData.AbandonMnemonic.Split(' ');

    // ---------- KDW-04 backup verification ----------
    [Fact]
    public void CreateNew_WithoutVerifiedBackup_WritesNothing()
    {
        var dir = TempDir();
        try
        {
            var m = Mgr(dir);
            var ch = new BackupChallenge(TestData.AbandonMnemonic);
            Assert.Throws<InvalidOperationException>(() => m.CreateNew(TestData.AbandonMnemonic, Pw, ch));
            Assert.False(ch.Verify(new[] { "zoo", "zoo", "zoo" }));
            Assert.Throws<InvalidOperationException>(() => m.CreateNew(TestData.AbandonMnemonic, Pw, ch));
            Assert.False(File.Exists(m.KeystorePath));
            Assert.Equal(1, ch.FailedAttempts);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CreateNew_AfterCorrectSpotCheck_Writes()
    {
        var dir = TempDir();
        try
        {
            var m = Mgr(dir);
            var ch = new BackupChallenge(TestData.AbandonMnemonic, 3);
            Assert.Equal(3, ch.Positions.Distinct().Count());
            Assert.All(ch.Positions, p => Assert.InRange(p, 1, 12));
            Assert.True(ch.Verify(ch.Positions.Select(p => " " + Words[p - 1].ToUpperInvariant() + " ").ToArray()));
            m.CreateNew(TestData.AbandonMnemonic, Pw, ch);
            Assert.True(File.Exists(m.KeystorePath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BackupChallenge_ForOtherMnemonic_Rejected()
    {
        var dir = TempDir();
        try
        {
            var other = MnemonicService.Generate(12);
            var ch = new BackupChallenge(other, 2);
            var ow = other.Split(' ');
            Assert.True(ch.Verify(ch.Positions.Select(p => ow[p - 1]).ToArray()));
            Assert.Throws<InvalidOperationException>(() => Mgr(dir).CreateNew(TestData.AbandonMnemonic, Pw, ch));
            Assert.DoesNotContain("abandon", ch.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BackupChallenge_PositionsVary()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 30; i++) seen.Add(string.Join(",", new BackupChallenge(TestData.AbandonMnemonic).Positions));
        Assert.True(seen.Count > 1);
    }

    // ---------- KDW-05 graceful unlock ----------
    private static (string dir, WalletManager m) CreatedWallet()
    {
        var dir = TempDir(); var m = Mgr(dir);
        m.CreateOrImport(TestData.AbandonMnemonic, Pw);
        return (dir, m);
    }

    [Fact]
    public void Unlock_EmptyPassword_ArgumentException_NoCounter()
    {
        var (dir, m) = CreatedWallet();
        try
        {
            Assert.Throws<ArgumentException>(() => m.Unlock(""));
            Assert.False(File.Exists(m.ThrottlePath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unlock_FourthAttempt_Throttled_AndPersistsAcrossRestart()
    {
        var (dir, m) = CreatedWallet();
        try
        {
            for (var i = 0; i < 3; i++) Assert.Throws<WrongPasswordException>(() => m.Unlock("wrong-password"));
            var ex = Assert.Throws<UnlockThrottledException>(() => m.Unlock(Pw));
            Assert.Contains("wait", ex.Message);
            // "restart": a fresh manager reads the persisted counter.
            var m2 = Mgr(dir);
            Assert.Throws<UnlockThrottledException>(() => m2.Unlock(Pw));
            var st = UnlockThrottle.Load(m2.ThrottlePath);
            Assert.Equal(3, st.Failures);
            // after the wait, the right password works and resets the counter
            st.LastFailureUtc = DateTimeOffset.UtcNow.AddMinutes(-5); st.Save(m2.ThrottlePath);
            m2.Unlock(Pw);
            Assert.True(m2.IsUnlocked);
            Assert.False(File.Exists(m2.ThrottlePath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Throttle_ClockSkew_CappedToDelay()
    {
        var t = new UnlockThrottle { Failures = 3, LastFailureUtc = DateTimeOffset.UtcNow.AddYears(5) };
        Assert.True(t.Remaining(DateTimeOffset.UtcNow) <= TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("garbage")]
    [InlineData("network")]
    [InlineData("salt")]
    [InlineData("kdf-huge")]
    [InlineData("null")]
    [InlineData("version")]
    public void CorruptKeystore_GivesKeystoreCorruptException_NeverOtherTypes(string kind)
    {
        var (dir, m) = CreatedWallet();
        try
        {
            var json = File.ReadAllText(m.KeystorePath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            string Mutate(string key, object v) { var d = doc.ToDictionary(k => k.Key, k => (object)k.Value); d[key] = v; return JsonSerializer.Serialize(d); }
            File.WriteAllText(m.KeystorePath, kind switch
            {
                "truncate" => json[..(json.Length / 2)],
                "garbage" => "\u0000\u0001not json",
                "network" => Mutate("Network", "Testnet11"),
                "salt" => Mutate("Salt", "!!!not-base64!!!"),
                "kdf-huge" => Mutate("Argon2MemoryKiB", 4 * 1024 * 1024),
                "null" => "null",
                "version" => Mutate("Version", 2),
                _ => json,
            });
            Assert.False(m.TryLoadKeystore(out _, out var err) && kind is not ("salt" or "version"));
            Assert.Null(Record.Exception(() => _ = m.Address)); // StartForm.Refresh_ path must not throw
            var ex = Record.Exception(() => m.Unlock(Pw));
            Assert.IsType<KeystoreCorruptException>(ex);
            Assert.DoesNotContain("abandon", ex!.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void KdfCaps_RejectDoSParameters()
    {
        Assert.Throws<KeystoreCorruptException>(() => new KdfParameters(KdfParameters.MaxMemoryKiB + 1, 3, 4).Validate());
        Assert.Throws<KeystoreCorruptException>(() => new KdfParameters(65536, 11, 4).Validate());
        Assert.Throws<KeystoreCorruptException>(() => new KdfParameters(65536, 3, 17).Validate());
        KdfParameters.Default.Validate();
    }

    [Fact]
    public void CorruptSettings_FallBackToSafeDefaults()
    {
        var dir = TempDir();
        try
        {
            var p = Path.Combine(dir, "settings.json");
            File.WriteAllText(p, "{ \"Network\": 12345, ");
            var s = WalletSettings.Load(p);
            Assert.Equal(WalletNetwork.Testnet10, s.Network);
            Assert.False(s.MainnetEnabled);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void KeystoreFile_IsOwnerOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var (dir, m) = CreatedWallet();
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(m.KeystorePath));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- KDW-11 folded change + fee cap ----------
    private static IReadOnlyList<Coin> OneKas(string addr) => new[] { TestData.FakeCoin(addr, 100_000_000, 7) };

    [Fact]
    public void DustChange_FoldedIntoFee_IsReportedWithAmount()
    {
        using var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
        var plan = TransferService.Build(acc, OneKas(acc.Address), TestData.AbandonTestnet, 90_000_000);
        Assert.Equal(0UL, plan.ChangeSompi);
        Assert.True(plan.FoldedChangeSompi > 9_000_000, $"folded={plan.FoldedChangeSompi}");
        Assert.Equal(plan.InputSompi - plan.AmountSompi - plan.FeeSompi + plan.FoldedChangeSompi, plan.FoldedChangeSompi);
        Assert.Single(plan.Warnings);
        Assert.Contains(Kas.Format(plan.FoldedChangeSompi), plan.Summary());
        Assert.Contains("ADDED TO THE FEE", plan.Summary());
    }

    [Fact]
    public void NormalChange_NoWarning()
    {
        using var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
        var plan = TransferService.Build(acc, OneKas(acc.Address), TestData.AbandonTestnet, 50_000_000);
        Assert.True(plan.ChangeSompi > 0);
        Assert.Equal(0UL, plan.FoldedChangeSompi);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void FeeCap_RejectsFoldedDustFee_WithExplanation()
    {
        using var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
        var s = new WalletSettings();
        var ex = Assert.Throws<TransferException>(() =>
            TransferService.Build(acc, OneKas(acc.Address), TestData.AbandonTestnet, 90_000_000, maxFeeSompi: s.FeeCapFor(90_000_000)));
        Assert.Contains("fee cap", ex.Message);
        Assert.Contains("too small for a change output", ex.Message);
        // normal send fits under the default cap
        var ok = TransferService.Build(acc, OneKas(acc.Address), TestData.AbandonTestnet, 50_000_000, maxFeeSompi: s.FeeCapFor(50_000_000));
        Assert.Equal(1_000_000UL, ok.FeeCapSompi);
    }

    [Fact]
    public void FeeCap_IsConfigurable()
    {
        var s = new WalletSettings { MaxFeeFloorSompi = 20_000_000, MaxFeePercent = 0 };
        Assert.Equal(20_000_000UL, s.FeeCapFor(90_000_000));
        Assert.Equal(5_000_000_000UL, new WalletSettings().FeeCapFor(500_000_000_000)); // 1% of 5000 KAS = 50 KAS
        using var acc = HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);
        var plan = TransferService.Build(acc, OneKas(acc.Address), TestData.AbandonTestnet, 90_000_000, maxFeeSompi: s.FeeCapFor(90_000_000));
        Assert.True(plan.FoldedChangeSompi > 0);
    }

    [Fact]
    public void ParseKas_Overflow_IsFormatException()
    {
        Assert.Throws<FormatException>(() => Kas.ParseKas("79228162514264337593543950335"));
    }

    // ---------- KDW-10 redaction formats ----------
    [Theory]
    [InlineData("{0}", ",")]
    [InlineData("{0}", ", ")]
    [InlineData("{0}", "-")]
    [InlineData("numbered", "")]
    [InlineData("numbered-space", "")]
    [InlineData("json", "")]
    public void Redactor_MasksStructuredMnemonics(string mode, string sep)
    {
        var w = Words;
        var text = mode switch
        {
            "numbered" => string.Join(" ", w.Select((x, i) => $"{i + 1}.{x}")),
            "numbered-space" => string.Join("\n", w.Select((x, i) => $"{i + 1}. {x}")),
            "json" => JsonSerializer.Serialize(w),
            _ => string.Join(sep, w),
        };
        var r = SecretRedactor.Redact("phrase: " + text + " end");
        Assert.DoesNotContain("abandon", r);
        Assert.Contains(SecretRedactor.Mask, r);
    }

    [Fact]
    public void Redactor_MasksBase64Key_KeepsAddresses()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray());
        Assert.Equal("k=" + SecretRedactor.Mask, SecretRedactor.Redact("k=" + key));
        Assert.Equal(TestData.AbandonTestnet, SecretRedactor.Redact(TestData.AbandonTestnet));
        const string normal = "Node call failed (WebSocketException); reconnecting in 2s, attempt 1 of 4.";
        Assert.Equal(normal, SecretRedactor.Redact(normal));
    }
}
