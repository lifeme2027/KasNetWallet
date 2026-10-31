using KaspaWallet.Core.Keystore;
using KaspaWallet.Core.Rpc;
using KaspaWallet.Core.Transfer;

namespace KaspaWallet.Core;

/// <summary>Application facade used by the UI: keystore file lifecycle, unlock/lock, node queries, transfer.</summary>
public sealed class WalletManager : IAsyncDisposable
{
    private HdAccount? _account;
    private IWalletNode? _node;

    public WalletManager(string directory, WalletSettings settings, KdfParameters? kdf = null)
    {
        Directory = directory;
        Settings = settings;
        Kdf = kdf ?? KdfParameters.Default;
    }

    public string Directory { get; }
    public WalletSettings Settings { get; }
    public KdfParameters Kdf { get; }
    public string KeystorePath => Path.Combine(Directory, $"wallet.{Settings.Network.ToString().ToLowerInvariant()}.json");
    public bool KeystoreExists => File.Exists(KeystorePath);
    public bool IsUnlocked => _account != null;
    public string? Address => _account?.Address ?? (KeystoreExists ? WalletKeystore.Load(KeystorePath).Address : null);

    /// <summary>Encrypts and saves; refuses to overwrite an existing keystore.</summary>
    public void CreateOrImport(string mnemonic, string password)
    {
        if (KeystoreExists) throw new InvalidOperationException("A wallet already exists for this network. Remove/back up the file first.");
        if (password is null || password.Length < 8) throw new ArgumentException("Password must be at least 8 characters.");
        var file = WalletKeystore.Encrypt(mnemonic, password, Settings.Network, Kdf);
        WalletKeystore.Save(file, KeystorePath);
    }

    public void Unlock(string password)
    {
        Lock();
        _account = WalletKeystore.Unlock(WalletKeystore.Load(KeystorePath), password);
    }

    public void Lock()
    {
        _account?.Dispose();
        _account = null;
    }

    public async Task<NodeStatus> ConnectAsync(CancellationToken ct = default)
    {
        if (_node != null) await _node.DisposeAsync();
        _node = null;
        var node = await WrpcWalletNode.ConnectAsync(Settings.EffectiveWrpcUrl, ct);
        var st = await node.GetStatusAsync(ct);
        _node = node;
        return st;
    }

    public void UseNode(IWalletNode node) => _node = node;

    private IWalletNode Node => _node ?? throw new InvalidOperationException("Not connected to a node.");
    private HdAccount Account => _account ?? throw new InvalidOperationException("Wallet is locked.");

    public async Task<(ulong balance, int utxos)> RefreshAsync(CancellationToken ct = default)
    {
        var addr = Address ?? throw new InvalidOperationException("No wallet.");
        var coins = await Node.GetCoinsAsync(addr, ct);
        ulong sum = 0;
        foreach (var c in coins) sum += c.Amount.Sompi;
        return (sum, coins.Count);
    }

    public async Task<TransferPlan> PrepareTransferAsync(string to, ulong amountSompi, FeeMode mode, CancellationToken ct = default)
    {
        var coins = await Node.GetCoinsAsync(Account.Address, ct);
        double? rate = mode == FeeMode.NodeEstimate ? await Node.GetNormalFeeRateAsync(ct) : null;
        return TransferService.Build(Account, coins, to, amountSompi, mode, rate);
    }

    public Task<string> BroadcastAsync(TransferPlan plan, bool userConfirmed, CancellationToken ct = default) =>
        TransferService.BroadcastAsync(Node, plan, userConfirmed, ct);

    public async ValueTask DisposeAsync()
    {
        Lock();
        if (_node != null) await _node.DisposeAsync();
    }
}
