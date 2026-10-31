using KasNetWallet.Core;
using KasNetWallet.Core.Keystore;
using KasNetWallet.Core.Transfer;
using Xunit;
using static KasNetWallet.Core.Tests.MvpReportFixTests;

namespace KasNetWallet.Core.Tests;

/// <summary>Retest-2 (round 3) fixes. Offline; no broadcast.</summary>
public class Round3Tests
{
    private static HdAccount Tn() => HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);

    // ---------- R3-5 ----------
    [Fact]
    public void R3_5_NoFeeJumpAt101_NodeEstimateMatchesRelayRule()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, Kas.SompiPerKas, 3) };
        var p100 = TransferService.Build(acc, coins, TestData.AbandonTestnet, 13_000_000, FeeMode.MinimumRelay);
        var p101 = TransferService.Build(acc, coins, TestData.AbandonTestnet, 13_000_000, FeeMode.NodeEstimate, nodeFeeRate: 101);
        // da66c4d N-G2: NodeEstimate now applies the rate to relay mass like MinimumRelay, so 100 → 101 no longer jumps.
        Assert.True(p101.FeeSompi <= p100.FeeSompi * 102 / 100, $"{p100.FeeSompi} -> {p101.FeeSompi}");
        Assert.DoesNotContain(p101.Warnings, w => w.Contains("Abnormally"));
        Assert.DoesNotContain(p100.Warnings, w => w.Contains("Abnormally"));
    }

    [Fact]
    public void R3_5_FeeHalfOfAmount_IsAbnormal_ConfigurableRatio()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 50 * Kas.SompiPerKas, 3), TestData.FakeCoin(acc.Address, 30_000_000, 4) };
        var strict = TransferService.Build(acc, coins, TestData.AbandonTestnet, 20_000_000, feePolicy: new FeePolicy { AbnormalFeeToAmountRatio = 0.00001 });
        Assert.Contains(strict.Warnings, w => w.Contains("Abnormally high fee relative to the amount"));
    }

    // ---------- R3-7 ----------
    [Fact]
    public void R3_7_SendMax_FillsMassLimit_AndAsksToConsolidate()
    {
        using var acc = Tn();
        var coins = Enumerable.Range(0, 200).Select(i => TestData.FakeCoin(acc.Address, (ulong)(10_000_000 + i * 1000), (byte)i, (uint)i)).ToArray();
        var mp = new MassPolicy { ToccataActive = false };
        var plan = TransferService.BuildSendMax(acc, coins, TestData.AbandonTestnet, massPolicy: mp);
        var n = plan.InputCount;
        Assert.InRange(n, 2, 199);
        Assert.Contains(plan.Warnings, w => w.Contains($"Only {n} of 200 UTXOs") && w.Contains("consolidate") && w.Contains("STAY"));
        // exactly filled: one more (largest-first) input no longer fits
        var ordered = coins.OrderByDescending(c => c.Amount.Sompi).ToArray();
        var withOneMore = TransferService.BuildSendMax(acc, ordered.Take(n + 1).ToArray(), TestData.AbandonTestnet, massPolicy: mp);
        Assert.Equal(n, withOneMore.InputCount); // n+1 does not fit → mass limit exactly filled
        var exact = TransferService.BuildSendMax(acc, ordered.Take(n).ToArray(), TestData.AbandonTestnet, massPolicy: mp);
        Assert.DoesNotContain(exact.Warnings, w => w.Contains("consolidate"));
        Assert.Equal(plan.AmountSompi, exact.AmountSompi); // the largest n coins were used
    }

    [Fact]
    public void R3_7_SendMax_AllFit_NoConsolidationWarning()
    {
        using var acc = Tn();
        var coins = Enumerable.Range(0, 5).Select(i => TestData.FakeCoin(acc.Address, 100_000_000, (byte)i, (uint)i)).ToArray();
        var plan = TransferService.BuildSendMax(acc, coins, TestData.AbandonTestnet);
        Assert.Equal(5, plan.InputCount);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("consolidate"));
    }

    // ---------- R3-4 ----------
    [Fact]
    public void R3_4_TinyAmount_SaysAmountTooSmall()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 50 * Kas.SompiPerKas, 3) };
        var ex = Assert.Throws<TransferException>(() =>
            TransferService.Build(acc, coins, TestData.AbandonTestnet, 2_000_000, massPolicy: new MassPolicy { ToccataActive = false }));
        Assert.Contains("Amount too small", ex.Message);
        Assert.Contains("storage mass", ex.Message);
    }

    // ---------- R3-1 / R3-3 ----------
    [Fact]
    public void R3_1_UnwritableState_FailsClosed_WithNotice()
    {
        var (dir, m) = CreatedWallet();
        try
        {
            Directory.CreateDirectory(m.ThrottlePath); // makes every write of the state file fail (works even as root)
            Assert.Throws<WrongPasswordException>(() => m.Unlock("wrong-password"));
            Assert.Contains("could not be saved", m.ThrottleNotice);
            var ex = Assert.Throws<UnlockThrottledException>(() => m.Unlock(Pw));
            Assert.Contains("wait", ex.Message);
            // a new manager in the same process still sees the strict state
            var m2 = Mgr(dir);
            Assert.Throws<UnlockThrottledException>(() => m2.Unlock(Pw));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void R3_3_OversizedStateFile_TreatedAsCorrupt()
    {
        var p = Path.Combine(Path.GetTempPath(), "kdw-thr-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(p, "{\"Failures\":5,\"Pad\":\"" + new string('x', 10_000) + "\"}");
        var st = UnlockThrottle.Load(p, DateTimeOffset.UtcNow, out var notice);
        Assert.Equal(0, st.Failures);
        Assert.NotNull(notice);
        Assert.False(File.Exists(p));
    }

    // ---------- R3-6 ----------
    [Fact]
    public void R3_6_Settings_EnumAsString_AndUnknownValueResetAlone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kdw-set-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(p, "{\"Network\":\"mainnet\",\"MainnetEnabled\":true,\"AutoLockMinutes\":7}");
            var s = WalletSettings.Load(p, out var n1);
            Assert.Equal(WalletNetwork.Mainnet, s.Network);
            Assert.Equal(7, s.AutoLockMinutes);
            File.WriteAllText(p, "{\"Network\":\"Testnet11\",\"AutoLockMinutes\":7}");
            var s2 = WalletSettings.Load(p, out var n2);
            Assert.Equal(new WalletSettings().Network, s2.Network);
            Assert.Equal(7, s2.AutoLockMinutes); // other settings kept
            Assert.Contains(n2, x => x.Contains("Network"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- R3-8 ----------
    private static readonly string[] W = TestData.AbandonMnemonic.Split(' ');

    [Fact]
    public void R3_8_Txid_And_RequestId_StayVisible()
    {
        var txid = string.Concat(Enumerable.Repeat("a1b2c3d4", 8));
        Assert.Contains(txid, SecretRedactor.Redact("Submitted txid " + txid));
        Assert.Contains(txid, SecretRedactor.Redact("transaction " + txid + " accepted"));
        var rid = "req_9fK2LmQ7xT";
        Assert.Contains(rid, SecretRedactor.Redact("request id " + rid));
        var guid = Guid.NewGuid().ToString();
        Assert.Contains(guid, SecretRedactor.Redact("request-id: " + guid));
        Assert.DoesNotContain(txid, SecretRedactor.Redact("private key: " + txid));
        Assert.DoesNotContain(txid, SecretRedactor.Redact("seed=" + txid));
    }

    [Fact]
    public void R3_8_LowercaseBase64Url_Key_Masked()
    {
        var key = "q3vx_9kd-7mz2pw0lr8tyh4ncu6bsa1jfe5gio-x_kz"; // 43 chars, lowercase+digits
        Assert.Equal(43, key.Length);
        Assert.DoesNotContain(key, SecretRedactor.Redact("k=" + key));
    }

    [Theory]
    [InlineData("wide")]
    [InlineData("li")]
    [InlineData("seven")]
    [InlineData("and")]
    [InlineData("then")]
    public void R3_8_MnemonicForms_Masked(string form)
    {
        string text = form switch
        {
            "wide" => string.Join(new string(' ', 60), W),
            "li" => "<ol>" + string.Concat(W.Select(w => "<li>" + w + "</li>")) + "</ol>",
            "seven" => string.Join(" ", W.Take(7).Select((w, i) => $"{i + 1}. {w}")),
            "and" => string.Join(" and ", W),
            _ => string.Join(", then ", W),
        };
        var r = SecretRedactor.Redact(text);
        Assert.DoesNotContain("abandon", r);
        Assert.Contains(SecretRedactor.Mask, r);
    }

    [Fact]
    public void R3_8_NormalProse_NotMasked()
    {
        const string s = "Please wait and then check the balance or open settings to change network";
        Assert.Equal(s, SecretRedactor.Redact(s));
    }
}
