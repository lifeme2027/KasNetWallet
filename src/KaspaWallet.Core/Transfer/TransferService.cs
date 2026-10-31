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

    private sealed record Ctx(HdAccount Account, Network Net, KaspaAddress Payee, KaspaAddress Own, Coin[] Ordered, FeeMode Mode, ulong Rate, ulong ReferenceRate, MassPolicy Mass);

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

        var built = TryBuild(c, c.Ordered, amountSompi, null, out var err)
            ?? throw new TransferException(err is not null && err.Contains("Not enough funds", StringComparison.OrdinalIgnoreCase)
                ? $"Amount plus fee exceeds your balance ({Kas.Format(total)} KAS). Use Send max to send everything minus the fee."
                : "Could not build transaction: " + err);

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

    /// <summary>Send max (N2): spends the largest UTXOs that fit the mass cap; amount = inputs − fee, no change output.</summary>
    public static TransferPlan BuildSendMax(HdAccount account, IReadOnlyList<Coin> coins, string to,
        FeeMode mode = FeeMode.MinimumRelay, double? nodeFeeRate = null, FeePolicy? feePolicy = null, MassPolicy? massPolicy = null)
    {
        var c = Prepare(account, coins, to, mode, nodeFeeRate, massPolicy);
        var byValue = c.Ordered.OrderByDescending(x => x.Amount.Sompi).ThenBy(x => x.Outpoint.TransactionId.ToString(), StringComparer.Ordinal).ThenBy(x => x.Outpoint.Index).ToList();
        var n = byValue.Count;
        string? lastError = null;
        while (n > 0)
        {
            var use = byValue.Take(n).ToArray();
            ulong total = 0;
            foreach (var x in use) total = checked(total + x.Amount.Sompi);
            // Initial over-estimate (≈1,200 g per input + 2,000 g overhead), then converge down to the exact expected fee.
            ulong fee = checked(c.Rate * ((ulong)use.Length * 1_200 + 2_000));
            Built? built = null, lastOk = null;
            var massTooHigh = false;
            for (var iter = 0; iter < 8; iter++)
            {
                if (total <= fee || total - fee < MinPaymentSompi)
                    throw new TransferException($"Balance ({Kas.Format(total)} KAS) is too small to cover the fee and the minimum payment of {Kas.Format(MinPaymentSompi)} KAS.");
                var amount = total - fee;
                var b = NewBuilder(c, use).Send(c.Payee, Money.FromSompi(amount)).SpendAllCoins().SendFees(Money.FromSompi(fee));
                Transaction tx;
                try { tx = b.BuildTransaction(sign: true); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    lastError = ex.Message;
                    massTooHigh = ex.Message.Contains("mass", StringComparison.OrdinalIgnoreCase);
                    if (massTooHigh) break;
                    throw new TransferException("Could not build send-max transaction: " + ex.Message);
                }
                Built v;
                try { v = Verify(c, b, tx, amount, allowChange: false); }
                catch (TransferException ex) when (ex.Message.Contains("mass", StringComparison.Ordinal)) { massTooHigh = true; lastError = ex.Message; break; }
                if (v.Expected <= fee) lastOk = v;
                if (v.Expected == fee) { built = v; break; }
                fee = v.Expected;
            }
            built ??= lastOk;
            if (built is not null)
                return Finish(c, built with { Expected = Math.Min(built.Expected, built.Fee) }, built.Tx.Outputs[0].Value.Sompi, feePolicy ?? FeePolicy.Default, true, 0, coins.Count);
            if (!massTooHigh) throw new TransferException("Could not compute a stable send-max fee: " + lastError);
            n = Math.Max(n - Math.Max(1, n / 4), 0); // drop the smallest 25% and retry
        }
        throw new TransferException("No UTXO combination fits the mass limit: " + lastError);
    }

    private static TransferPlan Finish(Ctx c, Built b, ulong amountSompi, FeePolicy policy, bool sendMax, int addedInputs, int availableCoins)
    {
        var warnings = new List<string>();
        var folded = sendMax ? 0 : b.Folded;
        if (folded > 0)
            warnings.Add($"Change of {Kas.Format(folded)} KAS is below the minimum change output ({Kas.Format(b.MinimumChange)} KAS) and no extra UTXO could make it valid. " +
                         "It will be ADDED TO THE FEE (paid to miners, not returned). Confirm to proceed, or change the amount / use Send max.");
        var effRate = b.FeeMass == 0 ? 0 : (double)b.Fee / b.FeeMass;
        var rateLimit = c.ReferenceRate * policy.RateMultiplier;
        if (effRate > rateLimit)
            warnings.Add($"Abnormally high fee: {Kas.Format(b.Fee)} KAS = {effRate:0} sompi/gram, more than {policy.RateMultiplier:0.#}× the reference rate ({c.ReferenceRate} sompi/gram).");
        var abs = policy.AbsoluteWarnThreshold(amountSompi);
        if (b.Fee > abs && effRate <= rateLimit)
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
