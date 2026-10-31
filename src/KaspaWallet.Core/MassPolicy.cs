using NKaspa;
using NKaspa.Policy;

namespace KaspaWallet.Core;

/// <summary>
/// Transaction mass policy, configurable per network instead of hardcoded.
/// ToccataActive=false → NKaspa pre-Toccata standard cap (100000). true → network post-Toccata block limits.
/// MaxTransactionMass, if set, further lowers the cap. Defaults are conservative (pre-Toccata) until
/// activation status per network is confirmed (docs/DECISIONS.md D10).
/// </summary>
public sealed class MassPolicy
{
    public bool ToccataActive { get; set; }
    public ulong? MaxTransactionMass { get; set; }

    public static MassPolicy DefaultFor(WalletNetwork n) => new() { ToccataActive = false };

    public ulong EffectiveCap(Network network)
    {
        var cap = ToccataActive ? network.MassLimits.Compute : MassCalculator.MaxStandardTransactionMass;
        return MaxTransactionMass is ulong m && m < cap ? m : cap;
    }

    public TransactionBuilder CreateBuilder(Network network)
    {
        var b = new TransactionBuilder(network, ToccataActive);
        b.MaximumMass = EffectiveCap(network);
        return b;
    }
}
