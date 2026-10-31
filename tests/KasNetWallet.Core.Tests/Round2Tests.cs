using System.Text.Json;
using KasNetWallet.Core.Keystore;
using KasNetWallet.Core.Transfer;
using NKaspa;
using Xunit;

namespace KasNetWallet.Core.Tests;

/// <summary>Retest round 2 (kdw-mvp1-retest N1–N7). Offline only; FakeNode never touches a network.</summary>
public class Round2Tests
{
    private static HdAccount Tn() => HdAccount.FromMnemonic(TestData.AbandonMnemonic, WalletNetwork.Testnet10);

    // ---------- N1 ----------
    [Fact]
    public void N1_CorruptThrottleFile_ResetsWithNotice()
    {
        var p = Path.Combine(Path.GetTempPath(), "kdw-thr-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, "{ not json");
        var st = UnlockThrottle.Load(p, DateTimeOffset.UtcNow, out var notice);
        Assert.Equal(0, st.Failures);
        Assert.Equal(TimeSpan.Zero, st.Remaining(DateTimeOffset.UtcNow));
        Assert.NotNull(notice);
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void N1_FutureTimestamp_ClampedAndPersisted()
    {
        var p = Path.Combine(Path.GetTempPath(), "kdw-thr-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new UnlockThrottle { Failures = 7, LastFailureUtc = DateTimeOffset.UtcNow.AddYears(3) }.Save(p);
            var now = DateTimeOffset.UtcNow;
            var st = UnlockThrottle.Load(p, now, out var notice);
            Assert.NotNull(notice);
            Assert.Equal(TimeSpan.Zero, st.Remaining(now));
            // second start: file already corrected, no notice, no wait
            var st2 = UnlockThrottle.Load(p, now.AddSeconds(1), out var n2);
            Assert.Null(n2);
            Assert.Equal(TimeSpan.Zero, st2.Remaining(now.AddSeconds(1)));
            Assert.True(st2.LastFailureUtc <= now);
        }
        finally { File.Delete(p); }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("future")]
    [InlineData("negative")]
    public void N1_Manager_UnlockWorks_AfterBadState(string kind)
    {
        var (dir, m) = MvpReportFixTests.CreatedWallet();
        try
        {
            switch (kind)
            {
                case "garbage": File.WriteAllText(m.ThrottlePath, "\0\0garbage"); break;
                case "future": File.WriteAllText(m.ThrottlePath, JsonSerializer.Serialize(new UnlockThrottle { Failures = 50, LastFailureUtc = DateTimeOffset.UtcNow.AddDays(400) })); break;
                case "negative": File.WriteAllText(m.ThrottlePath, "{\"Failures\":-5,\"LastFailureUtc\":\"2020-01-01T00:00:00Z\"}"); break;
            }
            m.Unlock(MvpReportFixTests.Pw);
            Assert.True(m.IsUnlocked);
            if (kind != "negative") Assert.NotNull(m.ThrottleNotice);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- N3 / N4 / N2 ----------
    [Fact]
    public void N3_DustChange_ExtraInputMakesValidChange()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 100_000_000, 7), TestData.FakeCoin(acc.Address, 50_000_000, 9) };
        var plan = TransferService.Build(acc, coins, TestData.AbandonTestnet, 99_000_000); // post-Toccata MinimumChange is 0.02 KAS
        Assert.Equal(0UL, plan.FoldedChangeSompi);
        Assert.True(plan.ChangeSompi >= 2_000_000, $"change={plan.ChangeSompi}");
        Assert.Equal(2, plan.InputCount);
        Assert.Empty(plan.Warnings);
        Assert.Equal(plan.ExpectedFeeSompi, plan.FeeSompi);
    }

    [Fact]
    public void N3_DustFold_NoExtraInput_WarnsAndNeedsAcknowledgement()
    {
        using var acc = Tn();
        var plan = TransferService.Build(acc, new[] { TestData.FakeCoin(acc.Address, 100_000_000, 7) }, TestData.AbandonTestnet, 99_000_000);
        Assert.True(plan.FoldedChangeSompi > 0);
        Assert.Contains(plan.Warnings, w => w.Contains("ADDED TO THE FEE")); // 5c49251: fee is no longer "abnormal" by rate, but the fold still needs acknowledgement
        var node = new TransferTests.FakeNode("testnet-10");
        Assert.ThrowsAsync<TransferException>(() => TransferService.BroadcastAsync(node, plan, userConfirmed: true)).GetAwaiter().GetResult();
        Assert.Equal(0, node.Submitted);
        TransferService.BroadcastAsync(node, plan, userConfirmed: true, warningsAcknowledged: true).GetAwaiter().GetResult();
        Assert.Equal(1, node.Submitted);
    }

    [Fact]
    public void N3_NoFixedAmountCap_LargeButNormalRateFee_WarnsNotRejects()
    {
        using var acc = Tn();
        // 60 small coins → large mass, fee rate normal (100), absolute fee above 0.01 KAS
        var coins = Enumerable.Range(0, 60).Select(i => TestData.FakeCoin(acc.Address, 20_000_000, (byte)i, (uint)i)).ToArray();
        var plan = TransferService.Build(acc, coins, TestData.AbandonTestnet, 1_100_000_000, feePolicy: new FeePolicy { WarnPercent = 0.01 });
        Assert.Equal(plan.ExpectedFeeSompi, plan.FeeSompi);
        Assert.True(plan.FeeSompi > 1_100_000, $"fee={plan.FeeSompi}");
        Assert.Contains(plan.Warnings, w => w.Contains("High fee"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Abnormally"));
    }

    [Fact]
    public void N4_ChangeBurnedByMassLimit_IsBlocked()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 50 * Kas.SompiPerKas, 3) };
        var ex = Assert.Throws<TransferException>(() =>
            TransferService.Build(acc, coins, TestData.AbandonTestnet, 10_000_000, FeeMode.NodeEstimate, nodeFeeRate: 150, massPolicy: new MassPolicy { ToccataActive = false }));
        Assert.Contains("burn", ex.Message);
        Assert.Contains("Send max", ex.Message);
    }

    [Fact]
    public void N2_SendMax_AmountIsBalanceMinusFee_NoChange()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 100_000_000, 1), TestData.FakeCoin(acc.Address, 250_000_000, 2), TestData.FakeCoin(acc.Address, 30_000_000, 3) };
        var plan = TransferService.BuildSendMax(acc, coins, TestData.AbandonTestnet);
        Assert.True(plan.IsSendMax);
        Assert.Equal(0UL, plan.ChangeSompi);
        Assert.Single(plan.Transaction.Outputs);
        Assert.Equal(380_000_000UL, plan.AmountSompi + plan.FeeSompi);
        Assert.Equal(plan.ExpectedFeeSompi, plan.FeeSompi);
        Assert.Empty(plan.Warnings);
        var r = TransferService.BuildSendMax(acc, coins, TestData.AbandonTestnet, FeeMode.NodeEstimate, 150);
        Assert.Equal(380_000_000UL, r.AmountSompi + r.FeeSompi);
        Assert.True(r.FeeSompi > plan.FeeSompi);
    }

    [Fact]
    public void N2_SendMax_SingleLargeUtxo_WorksWhereSmallSendIsBlocked()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 50 * Kas.SompiPerKas, 3) };
        var plan = TransferService.BuildSendMax(acc, coins, TestData.AbandonTestnet, FeeMode.NodeEstimate, 150);
        Assert.Equal(50 * Kas.SompiPerKas, plan.AmountSompi + plan.FeeSompi);
        Assert.True(plan.FeeSompi < 1_000_000);
    }

    [Fact]
    public void N2_SendMax_TooManyUtxos_UsesLargestThatFit()
    {
        using var acc = Tn();
        var coins = Enumerable.Range(0, 200).Select(i => TestData.FakeCoin(acc.Address, (ulong)(10_000_000 + i * 1000), (byte)i, (uint)i)).ToArray();
        var plan = TransferService.BuildSendMax(acc, coins, TestData.AbandonTestnet, massPolicy: new MassPolicy { ToccataActive = false });
        Assert.True(plan.InputCount < 200);
        Assert.Equal(200, plan.AvailableCoins);
        Assert.Contains($"of 200 UTXO", plan.Summary());
    }

    [Fact]
    public void N2_ErrorTexts_NeverAskForImpossibleFullBalanceSend()
    {
        using var acc = Tn();
        var coins = new[] { TestData.FakeCoin(acc.Address, 100_000_000, 7) };
        var ex = Assert.Throws<TransferException>(() => TransferService.Build(acc, coins, TestData.AbandonTestnet, 100_000_000));
        Assert.Contains("Send max", ex.Message);
        Assert.DoesNotContain("send the full balance", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- N5 ----------
    [Fact]
    public void N5_CorruptMessages_NotNested()
    {
        var inner = new KeystoreCorruptException("bad json");
        var outer = new KeystoreCorruptException(inner.Message);
        Assert.Equal(inner.Message, outer.Message);
        var (dir, m) = MvpReportFixTests.CreatedWallet();
        try
        {
            File.WriteAllText(m.KeystorePath, "{garbage");
            var ex = Assert.ThrowsAny<IOException>(() => m.Unlock(MvpReportFixTests.Pw));
            Assert.Equal(1, CountOf(ex.Message, "Restore it from backup"));
            Assert.Equal(1, CountOf(ex.Message, "corrupted or unsupported"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static int CountOf(string s, string sub) { var n = 0; for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + 1, StringComparison.Ordinal)) n++; return n; }

    // ---------- N6 ----------
    [Fact]
    public void N6_Settings_OutOfRange_ResetWithNotices()
    {
        var p = Path.Combine(Path.GetTempPath(), "kdw-set-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(p, "{\"RpcTimeoutSeconds\":-1,\"ClipboardClearSeconds\":0,\"AutoLockMinutes\":999999,\"ConfirmationDepthDaa\":0," +
                                 "\"WrpcUrl\":\"http://x\",\"Network\":42,\"FeePolicy\":{\"RateMultiplier\":0,\"WarnPercent\":-3,\"WarnFloorSompi\":0}}");
            var s = WalletSettings.Load(p, out var notes);
            var d = new WalletSettings();
            Assert.Equal(d.RpcTimeoutSeconds, s.RpcTimeoutSeconds);
            Assert.Equal(d.ClipboardClearSeconds, s.ClipboardClearSeconds);
            Assert.Equal(d.AutoLockMinutes, s.AutoLockMinutes);
            Assert.Equal(d.ConfirmationDepthDaa, s.ConfirmationDepthDaa);
            Assert.Equal(WalletNetwork.Testnet10, s.Network);
            Assert.Null(s.WrpcUrl);
            Assert.Equal(10, s.FeePolicy.RateMultiplier);
            Assert.Equal(1.0, s.FeePolicy.WarnPercent);
            Assert.Equal(1_000_000UL, s.FeePolicy.WarnFloorSompi);
            Assert.True(notes.Count >= 9, string.Join("|", notes));
            Assert.Empty(new WalletSettings().Normalize());
        }
        finally { File.Delete(p); }
    }

    // ---------- N7 ----------
    [Fact]
    public void N7_Base64Url_Redacted()
    {
        var key = "Ab3_-Zq9xY" + "Kp2_Lm-Np8Rs" + "Tu4Vw-Xy_5Za" + "Bc6De7Fg-";
        Assert.Equal(43, key.Length); // 32-byte key in unpadded base64url (round 3: only key-sized runs are masked)
        var r = SecretRedactor.Redact("token=" + key + " end");
        Assert.DoesNotContain(key, r);
        Assert.Contains(SecretRedactor.Mask, r);
        Assert.Contains("kaspatest:", SecretRedactor.Redact(TestData.AbandonTestnet));
    }

    [Theory]
    [InlineData("                    ")]       // 20 spaces
    [InlineData("\t\t\t\t\t\t")]
    [InlineData("  |  ")]
    [InlineData(" ,\n\n\n    - ")]
    [InlineData("  ·  ")]
    public void N7_WideSeparatorMnemonic_Redacted(string sep)
    {
        var words = TestData.AbandonMnemonic.Split(' ');
        var r = SecretRedactor.Redact("seed: " + string.Join(sep, words) + " (backup)");
        Assert.Contains(SecretRedactor.Mask, r);
        Assert.DoesNotContain("abandon", r);
    }
}
