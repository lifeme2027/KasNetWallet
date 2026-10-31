using NKaspa;

namespace KasNetWallet.Core.Rpc;

/// <summary>
/// Adds per-call timeout and reconnect-with-exponential-backoff around a connection factory
/// (NKaspa's KaspaWebSocketChannel has neither). Submit is NOT retried automatically after a transport
/// failure to avoid duplicate broadcasts — the caller re-checks via the UTXO set / tracker.
/// </summary>
public sealed class ResilientWalletNode : IWalletNode
{
    private readonly Func<CancellationToken, Task<IWalletNode>> _factory;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private IWalletNode? _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ResilientWalletNode(Func<CancellationToken, Task<IWalletNode>> factory, TimeSpan? timeout = null, int maxAttempts = 4,
        TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _factory = factory; _timeout = timeout ?? TimeSpan.FromSeconds(15); _maxAttempts = Math.Max(1, maxAttempts);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1); _maxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
        _delay = delay ?? Task.Delay;
    }

    public int Reconnects { get; private set; }
    public List<TimeSpan> Backoffs { get; } = new();

    public static ResilientWalletNode ForUrl(string url, TimeSpan timeout) =>
        new(async ct => await WrpcWalletNode.ConnectAsync(ValidateUrl(url), ct, timeout).ConfigureAwait(false), timeout);

    /// <summary>Only JSON wRPC over ws/wss is supported; gRPC/Borsh ports are rejected with a clear message.</summary>
    public static string ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "ws" && u.Scheme != "wss"))
            throw new ArgumentException("Node URL must be ws:// or wss:// (JSON wRPC).");
        if (u.Port is 16110 or 16210 or 16310)
            throw new ArgumentException("Port looks like gRPC; only JSON wRPC is supported (e.g. 18110 mainnet, 18210 tn10 — check node flags).");
        return u.ToString();
    }

    private async Task<T> Run<T>(Func<IWalletNode, CancellationToken, Task<T>> op, bool retry, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            try
            {
                var node = await EnsureAsync(cts.Token).ConfigureAwait(false);
                return await op(node, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsTransient(ex))
            {
                await DropAsync().ConfigureAwait(false);
                if (!retry || attempt >= _maxAttempts)
                    throw new TimeoutException($"Node unavailable after {attempt} attempt(s): {ex.GetType().Name}", ex);
                var d = TimeSpan.FromMilliseconds(Math.Min(_maxDelay.TotalMilliseconds, _baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
                Backoffs.Add(d);
                WalletLog.Warn($"Node call failed ({ex.GetType().Name}); reconnecting in {d.TotalSeconds:0.#}s");
                await _delay(d, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is OperationCanceledException or TimeoutException or System.Net.WebSockets.WebSocketException or IOException
            or System.Net.Http.HttpRequestException or ObjectDisposedException;

    private async Task<IWalletNode> EnsureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is null) { _inner = await _factory(ct).ConfigureAwait(false); Reconnects++; }
            return _inner;
        }
        finally { _gate.Release(); }
    }

    private async Task DropAsync()
    {
        var n = Interlocked.Exchange(ref _inner, null);
        if (n is not null) { try { await n.DisposeAsync().ConfigureAwait(false); } catch { } }
    }

    public Task<NodeStatus> GetStatusAsync(CancellationToken ct = default) => Run((n, t) => n.GetStatusAsync(t), true, ct);
    public Task<Money> GetBalanceAsync(string a, CancellationToken ct = default) => Run((n, t) => n.GetBalanceAsync(a, t), true, ct);
    public Task<IReadOnlyList<Coin>> GetCoinsAsync(string a, CancellationToken ct = default) => Run((n, t) => n.GetCoinsAsync(a, t), true, ct);
    public Task<double?> GetNormalFeeRateAsync(CancellationToken ct = default) => Run((n, t) => n.GetNormalFeeRateAsync(t), true, ct);
    public Task<string> SubmitAsync(Transaction tx, CancellationToken ct = default) => Run((n, t) => n.SubmitAsync(tx, t), false, ct);
    public ValueTask DisposeAsync() => new(DropAsync());
}
