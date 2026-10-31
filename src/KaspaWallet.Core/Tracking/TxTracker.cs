using System.Text.Json;
using NKaspa;

namespace KaspaWallet.Core.Tracking;

public enum TxState { Pending, Accepted, Confirmed, Reorged, Dropped }

/// <summary>Locally recorded outgoing tx. This is the wallet's history source (NKaspa has no history API).</summary>
public sealed class TrackedTx
{
    public string TxId { get; set; } = "";
    public string To { get; set; } = "";
    public ulong AmountSompi { get; set; }
    public ulong FeeSompi { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public ulong SubmittedDaaScore { get; set; }
    public List<string> SpentOutpoints { get; set; } = new();   // "txid:index"
    public bool HasChange { get; set; }
    public TxState State { get; set; }
    public ulong? AcceptedDaaScore { get; set; }
    public ulong Confirmations { get; set; }
}

/// <summary>
/// DAA-score based confirmation tracking with reorg handling. Each <see cref="Recheck"/> is evaluated from scratch
/// against the current UTXO set and virtual DAA score, so a state can move backwards:
///  - our change output (txid:*) present → Accepted, confirmations = virtualDaa − output.BlockDaaScore;
///    Confirmed once ≥ depth.
///  - spent inputs all gone but no change visible (no-change tx) → Accepted with unknown depth (treated as accepted).
///  - any spent input back in UTXO set after having been Accepted/Confirmed → Reorged (funds restored).
///  - still Pending after <c>dropAfterDaa</c> with inputs unspent → Dropped.
/// </summary>
public sealed class TxTracker
{
    private readonly List<TrackedTx> _txs = new();
    public ulong ConfirmationDepth { get; }
    public ulong DropAfterDaa { get; }

    public TxTracker(ulong confirmationDepth = 100, ulong dropAfterDaa = 600)
    { ConfirmationDepth = confirmationDepth; DropAfterDaa = dropAfterDaa; }

    public IReadOnlyList<TrackedTx> Items => _txs;

    public TrackedTx Add(string txId, string to, ulong amount, ulong fee, IEnumerable<OutPoint> spent, bool hasChange, ulong daa)
    {
        var t = new TrackedTx
        {
            TxId = txId, To = to, AmountSompi = amount, FeeSompi = fee, SubmittedAt = DateTimeOffset.Now,
            SubmittedDaaScore = daa, SpentOutpoints = spent.Select(Key).ToList(), HasChange = hasChange, State = TxState.Pending
        };
        _txs.Add(t);
        return t;
    }

    public static string Key(OutPoint o) => $"{o.TransactionId}:{o.Index}";

    public void Recheck(IReadOnlyList<Coin> utxos, ulong virtualDaa)
    {
        var set = utxos.ToDictionary(c => Key(c.Outpoint), c => c);
        foreach (var t in _txs)
        {
            if (t.State == TxState.Dropped) { if (t.SpentOutpoints.All(set.ContainsKey)) continue; }
            var inputsBack = t.SpentOutpoints.Count > 0 && t.SpentOutpoints.Any(set.ContainsKey);
            var change = utxos.FirstOrDefault(c => string.Equals(c.Outpoint.TransactionId.ToString(), t.TxId, StringComparison.OrdinalIgnoreCase));
            var wasIncluded = t.State is TxState.Accepted or TxState.Confirmed;

            if (change is not null)
            {
                t.AcceptedDaaScore = change.Utxo.BlockDaaScore;
                t.Confirmations = virtualDaa > change.Utxo.BlockDaaScore ? virtualDaa - change.Utxo.BlockDaaScore : 0;
                t.State = t.Confirmations >= ConfirmationDepth ? TxState.Confirmed : TxState.Accepted;
            }
            else if (!inputsBack && !t.HasChange)
            {
                t.AcceptedDaaScore ??= virtualDaa;
                t.Confirmations = virtualDaa - t.AcceptedDaaScore.Value;
                t.State = t.Confirmations >= ConfirmationDepth ? TxState.Confirmed : TxState.Accepted;
            }
            else if (wasIncluded || t.State == TxState.Reorged)
            {
                t.State = TxState.Reorged; t.Confirmations = 0; t.AcceptedDaaScore = null;
            }
            else if (inputsBack && virtualDaa >= t.SubmittedDaaScore + DropAfterDaa)
            {
                t.State = TxState.Dropped;
            }
            // else: still Pending (inputs spent in mempool or not yet seen)
        }
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_txs, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }

    public void Load(string path)
    {
        _txs.Clear();
        if (File.Exists(path)) _txs.AddRange(JsonSerializer.Deserialize<List<TrackedTx>>(File.ReadAllText(path)) ?? new());
    }
}
