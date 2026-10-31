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

/// <summary>Build → (user confirms) → broadcast. Signing uses NKaspa TransactionBuilder (BIP-340 Schnorr).</summary>
public static class TransferService
{
    /// <summary>Dust guard for the payment output (NKaspa merges change below 0.02 KAS into the fee).</summary>
    public const ulong MinPaymentSompi = 2_000_000;

    /// <summary>Offline: builds, signs and self-verifies a single-recipient transfer from the account's coins.</summary>
    public static TransferPlan Build(HdAccount account, IReadOnlyList<Coin> coins, string to, ulong amountSompi,
        FeeMode mode = FeeMode.MinimumRelay, double? nodeFeeRate = null, ulong? maxFeeSompi = null)
    {
        var net = account.Network.ToNKaspa();
        if (!KaspaAddress.TryParse(to?.Trim(), net, out var payee) || payee is null)
            throw new TransferException($"Recipient is not a valid {account.Network} address.");
        if (amountSompi < MinPaymentSompi) throw new TransferException($"Amount below minimum {Kas.Format(MinPaymentSompi)} KAS.");
        if (coins.Count == 0) throw new TransferException("No spendable UTXOs.");
        var own = KaspaAddress.Parse(account.Address, net);
        foreach (var c in coins)
            if (!c.ScriptPubKey.Equals(own.ScriptPubKey)) throw new TransferException("A coin does not belong to this wallet address.");

        // Deterministic input order: sort by txid then index.
        var ordered = coins.OrderBy(c => c.Outpoint.TransactionId.ToString(), StringComparer.Ordinal).ThenBy(c => c.Outpoint.Index).ToArray();
        ulong total = 0;
        foreach (var c in ordered) total = checked(total + c.Amount.Sompi);
        if (amountSompi >= total) throw new TransferException("Insufficient funds (amount + fee exceeds balance).");

        var builder = new TransactionBuilder(net).AddKeys(account.Key).AddCoins(ordered).Send(payee, Money.FromSompi(amountSompi)).SetChange(own);
        if (mode == FeeMode.NodeEstimate && nodeFeeRate is double r)
            builder.SendEstimatedFees(new FeeRate(Math.Max(FeeRate.MinimumRelaySompiPerGram, (ulong)Math.Ceiling(r))));
        else
            builder.SendMinimumRelayFee();

        Transaction tx;
        try { tx = builder.BuildTransaction(sign: true); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { throw new TransferException("Could not build transaction: " + ex.Message); }

        // Independent checks (mirrors NKaspa samples/Spend safeguards).
        var entries = new UtxoEntry[tx.Inputs.Count];
        ulong inSum = 0;
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var coin = ordered.FirstOrDefault(c => c.Outpoint.Equals(tx.Inputs[i].PreviousOutpoint))
                       ?? throw new TransferException("Input is not one of the wallet's coins.");
            entries[i] = coin.Utxo;
            inSum += coin.Amount.Sompi;
        }
        ulong outSum = 0, paid = 0, change = 0;
        foreach (var o in tx.Outputs)
        {
            outSum += o.Value.Sompi;
            if (o.ScriptPublicKey.Equals(payee.ScriptPubKey) && paid == 0 && o.Value.Sompi == amountSompi) paid = o.Value.Sompi;
            else if (o.ScriptPublicKey.Equals(own.ScriptPubKey)) change += o.Value.Sompi;
            else throw new TransferException("Unexpected output in built transaction.");
        }
        if (paid != amountSompi || outSum > inSum) throw new TransferException("Built transaction does not pay the requested amount.");
        if (!tx.Verify(entries)) throw new TransferException("Signature verification failed.");
        var fee = inSum - outSum;
        if (maxFeeSompi is ulong cap && fee > cap) throw new TransferException($"Fee {Kas.Format(fee)} KAS exceeds limit {Kas.Format(cap)} KAS.");
        var mass = MassCalculator.Calculate(tx, entries);
        return new TransferPlan(tx, account.Network, account.Address, payee.ToString(), amountSompi, fee, change, inSum, tx.Inputs.Count, MassCalculator.RelayMass(mass));
    }

    /// <summary>Submits only when the caller passes <paramref name="userConfirmed"/>=true and the node's network matches.</summary>
    public static async Task<string> BroadcastAsync(IWalletNode node, TransferPlan plan, bool userConfirmed, CancellationToken ct = default)
    {
        if (!userConfirmed) throw new TransferException("Broadcast requires explicit user confirmation.");
        if (plan.Broadcast) throw new TransferException("This transfer was already broadcast.");
        var status = await node.GetStatusAsync(ct).ConfigureAwait(false);
        if (!string.Equals(status.NetworkId, plan.Network.ExpectedNodeNetworkId(), StringComparison.OrdinalIgnoreCase))
            throw new TransferException($"Node network '{status.NetworkId}' does not match wallet network '{plan.Network.ExpectedNodeNetworkId()}'.");
        if (!status.IsSynced) throw new TransferException("Node is not synced.");
        var id = await node.SubmitAsync(plan.Transaction, ct).ConfigureAwait(false);
        plan.Broadcast = true;
        return id;
    }
}
