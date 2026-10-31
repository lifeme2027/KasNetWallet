using NKaspa;
using NKaspa.RPC;

namespace KaspaWallet.Core.Rpc;

/// <summary>wRPC (JSON over WebSocket) node connection using NKaspa's KaspaWebSocketChannel + KaspaWrpcJsonClient.</summary>
public sealed class WrpcWalletNode : IWalletNode
{
    private readonly KaspaWebSocketChannel? _channel;
    private readonly IKaspaRpcClient _rpc;

    private WrpcWalletNode(KaspaWebSocketChannel? channel, IKaspaRpcClient rpc)
    {
        _channel = channel;
        _rpc = rpc;
    }

    /// <summary>For tests: wrap any NKaspa RPC client (e.g. over a canned channel).</summary>
    public static WrpcWalletNode FromClient(IKaspaRpcClient rpc) => new(null, rpc);

    public static async Task<WrpcWalletNode> ConnectAsync(string url, CancellationToken ct = default)
    {
        var channel = new KaspaWebSocketChannel(new Uri(url));
        try
        {
            await channel.ConnectAsync(ct).ConfigureAwait(false);
            return new WrpcWalletNode(channel, new KaspaWrpcJsonClient(channel));
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<NodeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var i = await _rpc.GetServerInfoAsync(ct).ConfigureAwait(false);
        return new NodeStatus(i.ServerVersion, i.NetworkId, i.IsSynced, i.HasUtxoIndex, i.VirtualDaaScore);
    }

    public Task<Money> GetBalanceAsync(string address, CancellationToken ct = default) => _rpc.GetBalanceByAddressAsync(address, ct);

    public async Task<IReadOnlyList<Coin>> GetCoinsAsync(string address, CancellationToken ct = default)
    {
        var list = await _rpc.GetUtxosByAddressesAsync(new[] { address }, ct).ConfigureAwait(false);
        return list.Select(u => new Coin(u.Outpoint, u.Entry)).ToList();
    }

    public async Task<double?> GetNormalFeeRateAsync(CancellationToken ct = default)
    {
        var est = await _rpc.GetFeeEstimateAsync(ct).ConfigureAwait(false);
        if (est.NormalBuckets.Count > 0) return est.NormalBuckets[0].Feerate;
        return est.PriorityBucket?.Feerate;
    }

    public async Task<string> SubmitAsync(Transaction tx, CancellationToken ct = default) =>
        (await _rpc.SubmitTransactionAsync(tx, false, ct).ConfigureAwait(false)).ToString();

    public ValueTask DisposeAsync() => _channel?.DisposeAsync() ?? ValueTask.CompletedTask;
}
