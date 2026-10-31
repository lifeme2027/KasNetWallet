using NKaspa;

namespace KaspaWallet.Core.Transfer;

/// <summary>A built and signed transfer awaiting explicit user confirmation. Contains no private key.</summary>
public sealed class TransferPlan
{
    internal TransferPlan(Transaction tx, WalletNetwork network, string from, string to, ulong amountSompi, ulong feeSompi,
        ulong changeSompi, ulong inputSompi, int inputCount, ulong mass, ulong foldedChangeSompi = 0, ulong feeCapSompi = 0)
    {
        FoldedChangeSompi = foldedChangeSompi; FeeCapSompi = feeCapSompi;
        Transaction = tx; Network = network; From = from; To = to; AmountSompi = amountSompi; FeeSompi = feeSompi;
        ChangeSompi = changeSompi; InputSompi = inputSompi; InputCount = inputCount; Mass = mass;
        TransactionId = tx.GetId().ToString();
    }

    public Transaction Transaction { get; }
    public WalletNetwork Network { get; }
    public string From { get; }
    public string To { get; }
    public ulong AmountSompi { get; }
    public ulong FeeSompi { get; }
    public ulong ChangeSompi { get; }
    public ulong InputSompi { get; }
    public int InputCount { get; }
    public ulong Mass { get; }
    public string TransactionId { get; }
    public bool Broadcast { get; internal set; }

    /// <summary>Sompi that would have been change but was below the dust/minimum-change threshold and is paid as fee (KDW-11).</summary>
    public ulong FoldedChangeSompi { get; }
    /// <summary>Fee cap applied when building (0 = none).</summary>
    public ulong FeeCapSompi { get; }

    public IReadOnlyList<string> Warnings => FoldedChangeSompi == 0 ? Array.Empty<string>() : new[]
    {
        $"WARNING: change of {Kas.Format(FoldedChangeSompi)} KAS is below the minimum change output and will be ADDED TO THE FEE (paid to miners, not returned to you). " +
        "Send a slightly larger/smaller amount, or send your full balance, to avoid this."
    };

    public string Summary() =>
        $"Network: {Network}\nFrom: {From}\nTo: {To}\nAmount: {Kas.Format(AmountSompi)} KAS\nFee: {Kas.Format(FeeSompi)} KAS\n" +
        $"Change: {Kas.Format(ChangeSompi)} KAS\nInputs: {InputCount} ({Kas.Format(InputSompi)} KAS)\nMass: {Mass}\nTx id: {TransactionId}" +
        (FeeCapSompi > 0 ? $"\nFee cap: {Kas.Format(FeeCapSompi)} KAS" : "") +
        string.Concat(Warnings.Select(w => "\n\n" + w));
}

public static class Kas
{
    public const ulong SompiPerKas = 100_000_000;

    public static string Format(ulong sompi) =>
        (sompi / SompiPerKas).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
        (sompi % SompiPerKas).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parses a decimal KAS amount (invariant culture, max 8 decimals) to sompi.</summary>
    public static ulong ParseKas(string text)
    {
        if (!decimal.TryParse(text?.Trim(), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var kas))
            throw new FormatException("Amount is not a valid number.");
        if (kas <= 0) throw new FormatException("Amount must be positive.");
        decimal sompi;
        try { sompi = kas * SompiPerKas; } catch (OverflowException) { throw new FormatException("Amount too large."); }
        if (sompi != decimal.Truncate(sompi)) throw new FormatException("Amount has more than 8 decimals.");
        if (sompi > ulong.MaxValue) throw new FormatException("Amount too large.");
        return (ulong)sompi;
    }
}
