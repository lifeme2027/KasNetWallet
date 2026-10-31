using System.Text.Json.Serialization;
using NKaspa;
using NKaspa.Policy;
using NkMassPolicy = NKaspa.Policy.MassPolicy;

namespace KaspaWallet.Core;

/// <summary>
/// Wallet-side transaction mass policy (per network, settings.json). Since NKaspa 5c49251 the actual rules come from
/// <see cref="NkMassPolicy"/> (<c>Network.MassPolicyAt(daaScore)</c>):
/// <list type="bullet">
/// <item>ToccataActive = null (default, "auto"): decided from the node's virtual DAA score (<see cref="VirtualDaaScore"/>).
/// Without a DAA score: testnet → post-Toccata (testnet-10 has activated); mainnet → conservative mix (pre-Toccata 100,000 cap,
/// but 100 sompi/gram relay rate, so neither the mass nor the fee can be too low under either rule set).</item>
/// <item>true / false: forced post- / pre-Toccata NKaspa policy.</item>
/// </list>
/// MaxTransactionMass, if set, further lowers the cap (wallet-side guard).
/// </summary>
public sealed class MassPolicy
{
    public bool? ToccataActive { get; set; }
    public ulong? MaxTransactionMass { get; set; }

    /// <summary>Runtime only: the node's virtual DAA score, used when <see cref="ToccataActive"/> is null.</summary>
    [JsonIgnore] public ulong? VirtualDaaScore { get; set; }

    public static MassPolicy DefaultFor(WalletNetwork n) => new();

    public MassPolicy WithDaaScore(ulong? daa) => new() { ToccataActive = ToccataActive, MaxTransactionMass = MaxTransactionMass, VirtualDaaScore = daa };

    public NkMassPolicy Resolve(Network network)
    {
        if (ToccataActive is bool forced) return forced ? NkMassPolicy.PostToccata : NkMassPolicy.PreToccata;
        if (VirtualDaaScore is ulong daa && daa > 0) return network.MassPolicyAt(daa);
        if (network != Network.Mainnet) return NkMassPolicy.PostToccata;
        var pre = NkMassPolicy.PreToccata;
        return new NkMassPolicy(false, pre.BlockLimits, pre.MaximumTransactionMass, NkMassPolicy.PostToccataMinimumRelayFeeRate);
    }

    public ulong EffectiveCap(Network network)
    {
        var cap = Resolve(network).MaximumTransactionMass;
        return MaxTransactionMass is ulong m && m < cap ? m : cap;
    }

    public TransactionBuilder CreateBuilder(Network network)
    {
        var b = new TransactionBuilder(network, Resolve(network));
        b.MaximumMass = EffectiveCap(network);
        return b;
    }
}
