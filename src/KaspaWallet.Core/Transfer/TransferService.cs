using NKaspa;
using NKaspa.Policy;
using KaspaWallet.Core.Rpc;

namespace KaspaWallet.Core.Transfer;

public enum FeeMode
{
    /// <summary>NKaspa SendMinimumRelayFee().</summary>
    MinimumRelay,
    /// <summary>Node fee estimate (normal bucket), clamped to NKaspa's 100 sompi/gram floor.</summary>
    NodeEstimate,
}

public sealed class TransferException : Exception
{
    public TransferException(string message) : base(message) { }
}

/// <summary>Submission timed out/disconnected: the tx may or may not be accepted. Re-submitting the same signed tx cannot double-spend.</summary>
public sealed class TransferStatusUnknownException : Exception
{
    public TransferStatusUnknownException(string txId) : base($"Submission status unknown (node did not answer). The transaction {txId} may already be in the network — refresh balance/history before trying again.") => TransactionId = txId;
    public string TransactionId { get; }
}

/// <summary>Build → (user confirms, incl. warnings) → broadcast. Signing uses NKaspa TransactionBuilder (BIP-340 Schnorr).</summary>
public static class TransferService
{
    /// <summary>Dust guard for the payment output.</summary>
    public const ulong MinPaymentSompi = 2_000_000;
    /// <summary>Fee above the computed expected fee by more than this is treated as folded change.</summary>
    internal const ulong FoldToleranceSompi = 1_000;
    private const int MaxExtraInputTries = 64;

    private sealed record Built(Transaction Tx, UtxoEntry[] Entries, ulong InSum, ulong Fee, ulong Change, ulong Expected, ulong FeeMass, ulong RelayMass, ulong MinimumChange)
    {
        public ulong Folded => Change == 0 && Fee > Expected + FoldToleranceSompi ? Fee - Expected : 0;
    }

    internal sealed record Ctx(HdAccount Account, Network Net, KaspaAddress Payee, KaspaAddress Own, Coin[] Ordered, FeeMode Mode, ulong Rate, ulong ReferenceRate, MassPolicy Mass);

    /// <summary>
    /// R3-4: KIP-9 storage mass of an output is roughly 10^12 / value (sompi), so outputs below about 10^12 / mass-cap
    /// (0.1 KAS with the 100,000 pre-Toccata cap) cannot fit; ×2 margin because the change output adds storage mass too.
    /// </summary>
    internal static ulong SmallAmountThreshold(Ctx c) => 2 * (1_000_000_000_000UL / Math.Max(1UL, c.Mass.EffectiveCap(c.Net)));

    private static string SmallAmountText(Ctx c, ulong amountSompi) =>
        $"Amount too small: {Kas.Format(amountSompi)} KAS would create a very small output whose storage mass (KIP-9) exceeds the transaction mass limit " +
        $"({c.Mass.EffectiveCap(c.Net)}). Send at least about {Kas.Format(SmallAmountThreshold(c))} KAS, or combine several small payments into one.";

    private static Ctx Prepare(HdAccount account, IReadOnlyList<Coin> coins, string to, FeeMode mode, double? nodeFeeRate, MassPolicy? massPolicy)
    {
        var net = account.Network.ToNKaspa();
        if (!KaspaAddress.TryParse(to?.Trim(), net, out var payee) || payee is null)
            throw new TransferException($"Recipient is not a valid {account.Network} address.");
        if (coins.Count == 0) throw new TransferException("No spendable UTXOs.");
        var own = KaspaAddress.Parse(account.Address, net);
        foreach (var c in coins)
            if (!c.ScriptPubKey.Equals(own.ScriptPubKey)) throw new TransferException("A coin does not belong to this wallet address.");
        // Deterministic input order: sort by txid then index.
        var ordered = coins.OrderBy(c => c.Outpoint.TransactionId.ToString(), StringComparer.Ordinal).ThenBy(c => c.Outpoint.Index).ToArray();
        var estimate = nodeFeeRate is double r && r > 0 && !double.IsNaN(r) ? (ulong)Math.Min(1e12, Math.Ceiling(r)) : 0UL;
        var rate = mode == FeeMode.NodeEstimate ? Math.Max(FeeRate.MinimumRelaySompiPerGram, estimate) : FeeRate.MinimumRelaySompiPerGram;
        var reference = Math.Max(FeeRate.MinimumRelaySompiPerGram, estimate);
        return new Ctx(account, net, payee, own, ordered, mode, rate, reference, massPolicy ?? MassPolicy.DefaultFor(account.Network));
    }

    private static TransactionBuilder NewBuilder(Ctx c, IEnumerable<Coin> coins)
    {
        var b = c.Mass.CreateBuilder(c.Net).AddKeys(c.Account.Key).AddCoins(coins.ToArray()).SetChange(c.Own);
        return b;
    }

    private static void ApplyFeeMode(Ctx c, TransactionBuilder b)
    {
        if (c.Mode == FeeMode.NodeEstimate) b.SendEstimatedFees(new FeeRate(c.Rate));
        else b.SendMinimumRelayFee();
    }

    /// <summary>Expected fee for this exact tx under the chosen mode (what NKaspa should charge without folding).</summary>
    private static ulong ExpectedFee(Ctx c, TransactionMass m) =>
        c.Mode == FeeMode.NodeEstimate ? checked(m.FeeMass * c.Rate) : MassCalculator.MinimumRelayFee(m);

    private static Built Verify(Ctx c, TransactionBuilder b, Transaction tx, ulong amountSompi, bool allowChange)
    {
        var entries = new UtxoEntry[tx.Inputs.Count];
        ulong inSum = 0;
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var coin = c.Ordered.FirstOrDefault(x => x.Outpoint.Equals(tx.Inputs[i].PreviousOutpoint))
                       ?? throw new TransferException("Input is not one of the wallet's coins.");
            entries[i] = coin.Utxo;
            inSum += coin.Amount.Sompi;
        }
        ulong outSum = 0, paid = 0, change = 0;
        foreach (var o in tx.Outputs)
        {
            outSum += o.Value.Sompi;
            if (o.ScriptPublicKey.Equals(c.Payee.ScriptPubKey) && paid == 0 && o.Value.Sompi == amountSompi) paid = o.Value.Sompi;
            else if (o.ScriptPublicKey.Equals(c.Own.ScriptPubKey)) change += o.Value.Sompi;
            else throw new TransferException("Unexpected output in built transaction.");
        }
        if (paid != amountSompi || outSum > inSum) throw new TransferException("Built transaction does not pay the requested amount.");
        if (!allowChange && change != 0) throw new TransferException("Send-max transaction unexpectedly has change.");
        if (!tx.Verify(entries)) throw new TransferException("Signature verification failed.");
        var mass = MassCalculator.Calculate(tx, entries, b.MassLimits);
        var relay = MassCalculator.RelayMass(mass);
        if (Math.Max(relay, mass.FeeMass) > c.Mass.EffectiveCap(c.Net))
            throw new TransferException($"Transaction mass {Math.Max(relay, mass.FeeMass)} exceeds the configured cap {c.Mass.EffectiveCap(c.Net)}. Use fewer inputs: send a smaller amount, or use Send max (it picks the largest UTXOs that fit).");
        return new Built(tx, entries, inSum, inSum - outSum, change, ExpectedFee(c, mass), mass.FeeMass, relay, b.MinimumChange.Sompi);
    }

    private static Built? TryBuild(Ctx c, IEnumerable<Coin> pool, ulong amountSompi, Coin[]? mustSpend, out string? error)
    {
        error = null;
        var b = NewBuilder(c, pool).Send(c.Payee, Money.FromSompi(amountSompi));
        if (mustSpend is { Length: > 0 }) b.MustSpend(mustSpend.Select(x => x.Outpoint).ToArray());
        ApplyFeeMode(c, b);
        Transaction tx;
        try { tx = b.BuildTransaction(sign: true); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { error = ex.Message; return null; }
        return Verify(c, b, tx, amountSompi, allowChange: true);
    }

    /// <summary>Offline: builds, signs and self-verifies a single-recipient transfer from the account's coins.</summary>
    public static TransferPlan Build(HdAccount account, IReadOnlyList<Coin> coins, string to, ulong amountSompi,
        FeeMode mode = FeeMode.MinimumRelay, double? nodeFeeRate = null, FeePolicy? feePolicy = null, MassPolicy? massPolicy = null)
    {
        var c = Prepare(account, coins, to, mode, nodeFeeRate, massPolicy);
        if (amountSompi < MinPaymentSompi) throw new TransferException($"Amount below minimum {Kas.Format(MinPaymentSompi)} KAS.");
        ulong total = 0;
        foreach (var x in c.Ordered) total = checked(total + x.Amount.Sompi);
        if (amountSompi >= total)
            throw new TransferException($"Amount plus fee exceeds your balance ({Kas.Format(total)} KAS). Use Send max to send everything minus the fee.");

        Built? built;
        try { built = TryBuild(c, c.Ordered, amountSompi, null, out var err0); if (built is null) throw new TransferException(err0 ?? "unknown error"); }
        catch (TransferException ex)
        {
            var err = ex.Message;
            if (err.Contains("Not enough funds", StringComparison.OrdinalIgnoreCase))
                throw new TransferException($"Amount plus fee exceeds your balance ({Kas.Format(total)} KAS). Use Send max to send everything minus the fee.");
            if (err.Contains("mass", StringComparison.OrdinalIgnoreCase) && amountSompi < SmallAmountThreshold(c))
                throw new TransferException(SmallAmountText(c, amountSompi));
            throw new TransferException(err.StartsWith("Transaction mass", StringComparison.Ordinal) ? err : "Could not build transaction: " + err);
        }

        var addedInputs = 0;
        if (built.Folded > 0 && built.Folded < built.MinimumChange)
        {
            // Dust change: try adding one more input so the change becomes a valid output (N3).
            var selected = built.Tx.Inputs.Select(i => c.Ordered.First(x => x.Outpoint.Equals(i.PreviousOutpoint))).ToArray();
            var rest = c.Ordered.Where(x => !selected.Contains(x)).OrderBy(x => x.Amount.Sompi).Take(MaxExtraInputTries);
            foreach (var extra in rest)
            {
                var alt = TryBuild(c, c.Ordered, amountSompi, selected.Append(extra).ToArray(), out _);
                if (alt is not null && alt.Folded == 0) { built = alt; addedInputs = 1; break; }
            }
        }

        if (built.Folded >= built.MinimumChange)
        {
            // N4 (NKaspa): change would be valid but a change output would exceed the mass limit, so the whole change is burned as fee. Block.
            throw new TransferException(
                $"Blocked: this transaction would burn {Kas.Format(built.Folded)} KAS of change as fee, because adding a change output exceeds the mass limit " +
                "(very small payments from large UTXOs create high storage mass). Try a larger amount, or use Send max.");
        }
        return Finish(c, built, amountSompi, feePolicy ?? FeePolicy.Default, false, addedInputs, coins.Count);
    }

    /// <summary>
    /// Send max (N2): amount = inputs − fee, no change output. When all UTXOs exceed the mass cap, a binary search finds the
    /// largest number of (largest-first) UTXOs that still fits (R3-7); the plan then carries a consolidation warning.
    /// </summary>
    public static TransferPlan BuildSendMax(HdAccount account, IReadOnlyList<Coin> coins, string to,
        FeeMode mode = FeeMode.MinimumRelay, double? nodeFeeRate = null, FeePolicy? feePolicy = null, MassPolicy? massPolicy = null)
    {
        var c = Prepare(account, coins, to, mode, nodeFeeRate, massPolicy);
        var byValue = c.Ordered.OrderByDescending(x => x.Amount.Sompi).ThenBy(x => x.Outpoint.TransactionId.ToString(), StringComparer.Ordinal).ThenBy(x => x.Outpoint.Index).ToList();
        string? lastError = null;
        var all = TrySendMax(c, byValue, byValue.Count, ref lastError);
        Built? best = all;
        var used = byValue.Count;
        if (best is null)
        {
            int lo = 1, hi = byValue.Count - 1; // lo..hi = candidate counts; find largest feasible
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var r = TrySendMax(c, byValue, mid, ref lastError);
                if (r is not null) { best = r; used = mid; lo = mid + 1; } else hi = mid - 1;
            }
            // mass is not perfectly monotonic (storage mass): probe a few counts above the found one
            for (var k = used + 1; best is not null && k <= Math.Min(byValue.Count - 1, used + 8); k++)
                if (TrySendMax(c, byValue, k, ref lastError) is Built r2) { best = r2; used = k; }
        }
        if (best is null) throw new TransferException("Even a single UTXO does not fit the mass limit: " + lastError);
        ulong remaining = 0;
        foreach (var x in byValue.Skip(used)) remaining += x.Amount.Sompi;
        var extra = used < byValue.Count
            ? $"Only {used} of {byValue.Count} UTXOs fit in one transaction (mass limit). {Kas.Format(remaining)} KAS in {byValue.Count - used} UTXO(s) will STAY in this wallet. " +
              "To send everything, consolidate UTXOs first: use Send max to your OWN address (repeat until 1 UTXO is left), then Send max to the recipient."
            : null;
        return Finish(c, best with { Expected = Math.Min(best.Expected, best.Fee) }, best.Tx.Outputs[0].Value.Sompi, feePolicy ?? FeePolicy.Default, true, 0, coins.Count, extra);
    }

    /// <summary>null = does not fit the mass cap. Throws for other errors (e.g. balance too small).</summary>
    private static Built? TrySendMax(Ctx c, List<Coin> byValue, int n, ref string? lastError)
    {
        var use = byValue.Take(n).ToArray();
        ulong total = 0;
        foreach (var x in use) total = checked(total + x.Amount.Sompi);
        // Initial over-estimate (≈1,200 g per input + 2,000 g overhead), then converge to the exact expected fee.
        ulong fee = checked(c.Rate * ((ulong)use.Length * 1_200 + 2_000));
        Built? lastOk = null;
        for (var iter = 0; iter < 8; iter++)
        {
            if (total <= fee || total - fee < MinPaymentSompi)
            {
                if (lastOk is not null) return lastOk;
                if (n < byValue.Count) { lastError = "balance of the fitting UTXOs is too small"; return null; }
                throw new TransferException($"Balance ({Kas.Format(total)} KAS) is too small to cover the fee and the minimum payment of {Kas.Format(MinPaymentSompi)} KAS.");
            }
            var amount = total - fee;
            var b = NewBuilder(c, use).Send(c.Payee, Money.FromSompi(amount)).SpendAllCoins().SendFees(Money.FromSompi(fee));
            Transaction tx;
            try { tx = b.BuildTransaction(sign: true); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                lastError = ex.Message;
                if (ex.Message.Contains("mass", StringComparison.OrdinalIgnoreCase)) return null;
                throw new TransferException("Could not build send-max transaction: " + ex.Message);
            }
            Built v;
            try { v = Verify(c, b, tx, amount, allowChange: false); }
            catch (TransferException ex) when (ex.Message.Contains("mass", StringComparison.Ordinal)) { lastError = ex.Message; return null; }
            if (v.Expected <= fee) lastOk = v;
            if (v.Expected == fee) return v;
            fee = v.Expected;
        }
        return lastOk ?? throw new TransferException("Could not compute a stable send-max fee: " + lastError);
    }

    private static TransferPlan Finish(Ctx c, Built b, ulong amountSompi, FeePolicy policy, bool sendMax, int addedInputs, int availableCoins, string? extraWarning = null)
    {
        var warnings = new List<string>();
        if (extraWarning is not null) warnings.Add(extraWarning);
        var folded = sendMax ? 0 : b.Folded;
        if (folded > 0)
            warnings.Add($"Change of {Kas.Format(folded)} KAS is below the minimum change output ({Kas.Format(b.MinimumChange)} KAS) and no extra UTXO could make it valid. " +
                         "It will be ADDED TO THE FEE (paid to miners, not returned). Confirm to proceed, or change the amount / use Send max.");
        var effRate = b.FeeMass == 0 ? 0 : (double)b.Fee / b.FeeMass;
        var rateLimit = c.ReferenceRate * policy.RateMultiplier;
        var storageDriven = b.FeeMass > b.RelayMass;
        var relRatio = amountSompi == 0 ? double.PositiveInfinity : (double)b.Fee / amountSompi;
        if (relRatio >= policy.AbnormalFeeToAmountRatio)
            warnings.Add($"Abnormally high fee relative to the amount: fee {Kas.Format(b.Fee)} KAS is {relRatio * 100:0}% of the {Kas.Format(amountSompi)} KAS sent." +
                (storageDriven ? " Cause: storage mass (KIP-9) — small outputs are very heavy, and at fee rates above the 100 sompi/gram minimum storage mass is paid in full. Send a larger amount, combine payments, or use the Minimum relay fee mode." : ""));
        else if (effRate > rateLimit)
            warnings.Add($"Abnormally high fee: {Kas.Format(b.Fee)} KAS = {effRate:0} sompi/gram, more than {policy.RateMultiplier:0.#}× the reference rate ({c.ReferenceRate} sompi/gram).");
        var abs = policy.AbsoluteWarnThreshold(amountSompi);
        if (b.Fee > abs && effRate <= rateLimit && relRatio < policy.AbnormalFeeToAmountRatio)
            warnings.Add($"High fee: {Kas.Format(b.Fee)} KAS is more than {Kas.Format(abs)} KAS (max of {Kas.Format(policy.WarnFloorSompi)} KAS and {policy.WarnPercent:0.##}% of the amount). The fee rate itself is normal ({effRate:0} sompi/gram); the fee is high because of the transaction size.");
        return new TransferPlan(b.Tx, c.Account.Network, c.Account.Address, c.Payee.ToString(), amountSompi, b.Fee, b.Change, b.InSum, b.Tx.Inputs.Count,
            b.RelayMass, folded, warnings, sendMax, addedInputs, availableCoins, b.Expected);
    }

    /// <summary>Submits only when the caller passes <paramref name="userConfirmed"/>=true and the node's network matches.</summary>
    public static async Task<string> BroadcastAsync(IWalletNode node, TransferPlan plan, bool userConfirmed, CancellationToken ct = default, bool warningsAcknowledged = false)
    {
        if (!userConfirmed) throw new TransferException("Broadcast requires explicit user confirmation.");
        if (plan.Warnings.Count > 0 && !warningsAcknowledged) throw new TransferException("This transfer has fee warnings that must be acknowledged before broadcasting.");
        if (plan.Broadcast) throw new TransferException("This transfer was already broadcast.");
        var status = await node.GetStatusAsync(ct).ConfigureAwait(false);
        if (!string.Equals(status.NetworkId, plan.Network.ExpectedNodeNetworkId(), StringComparison.OrdinalIgnoreCase))
            throw new TransferException($"Node network '{status.NetworkId}' does not match wallet network '{plan.Network.ExpectedNodeNetworkId()}'.");
        if (!status.IsSynced) throw new TransferException("Node is not synced.");
        string id;
        try { id = await node.SubmitAsync(plan.Transaction, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            throw new TransferStatusUnknownException(plan.TransactionId);
        }
        plan.Broadcast = true;
        return id;
    }
}
