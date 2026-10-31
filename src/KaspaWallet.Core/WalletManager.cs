using KaspaWallet.Core.Keystore;
using KaspaWallet.Core.Rpc;
using KaspaWallet.Core.Transfer;
using KaspaWallet.Core.Tracking;
using System.Text;

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
        password = NormalizePassword(password);
        var file = WalletKeystore.Encrypt(mnemonic, password, Settings.Network, Kdf);
        WalletKeystore.Save(file, KeystorePath);
    }

    public void Unlock(string password)
    {
        Lock();
        var wait = _failedUnlocks > 2 ? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(5, _failedUnlocks - 2))) : TimeSpan.Zero;
        if (DateTimeOffset.UtcNow < _lastFailure + wait) throw new InvalidOperationException($"Too many attempts; wait {wait.TotalSeconds:0}s.");
        try { _account = WalletKeystore.Unlock(WalletKeystore.Load(KeystorePath), NormalizePassword(password, checkStrength: false)); _failedUnlocks = 0; }
        catch (WrongPasswordException) { _failedUnlocks++; _lastFailure = DateTimeOffset.UtcNow; throw; }
    }

    private int _failedUnlocks;
    private DateTimeOffset _lastFailure;

    /// <summary>NFKC-normalizes; empty passwords are always forbidden; new passwords need ≥8 chars.</summary>
    public static string NormalizePassword(string password, bool checkStrength = true)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password must not be empty.");
        var n = password.Normalize(NormalizationForm.FormKC);
        if (checkStrength && n.Length < 8) throw new ArgumentException("Password must be at least 8 characters.");
        return n;
    }

    /// <summary>Re-encrypts the keystore under a new password (fresh salt/nonce). Old password stops working.</summary>
    public void ChangePassword(string oldPassword, string newPassword)
    {
        var file = WalletKeystore.Load(KeystorePath);
        var phrase = WalletKeystore.Decrypt(file, NormalizePassword(oldPassword, false));
        var updated = WalletKeystore.Encrypt(phrase, NormalizePassword(newPassword), Settings.Network, Kdf);
        WalletKeystore.Save(updated, KeystorePath);
    }

    public string HistoryPath => Path.Combine(Directory, $"history.{Settings.Network.ToString().ToLowerInvariant()}.json");
    public TxTracker Tracker { get; private set; } = new();

    /// <summary>Re-checks every tracked tx against the current UTXO set + virtual DAA score (reorg aware).</summary>
    public async Task<IReadOnlyList<TrackedTx>> RecheckHistoryAsync(CancellationToken ct = default)
    {
        var st = await Node.GetStatusAsync(ct);
        var coins = await Node.GetCoinsAsync(Address!, ct);
        Tracker.Recheck(coins, st.VirtualDaaScore);
        Tracker.Save(HistoryPath);
        return Tracker.Items;
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
        if (Settings.Network == WalletNetwork.Mainnet && !Settings.MainnetEnabled)
            throw new InvalidOperationException("Mainnet is disabled. Enable it explicitly in settings.");
        var node = ResilientWalletNode.ForUrl(Settings.EffectiveWrpcUrl, TimeSpan.FromSeconds(Settings.RpcTimeoutSeconds));
        NodeStatus st;
        try { st = await node.GetStatusAsync(ct); }
        catch { await node.DisposeAsync(); throw; }
        if (!string.Equals(st.NetworkId, Settings.Network.ExpectedNodeNetworkId(), StringComparison.OrdinalIgnoreCase))
        { await node.DisposeAsync(); throw new InvalidOperationException($"Node network '{st.NetworkId}' does not match '{Settings.Network.ExpectedNodeNetworkId()}'."); }
        Tracker.Load(HistoryPath);
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
        return TransferService.Build(Account, coins, to, amountSompi, mode, rate, null, Settings.MassPolicyFor(Settings.Network));
    }

    public async Task<string> BroadcastAsync(TransferPlan plan, bool userConfirmed, CancellationToken ct = default)
    {
        if (plan.Network == WalletNetwork.Mainnet && !Settings.MainnetEnabled) throw new TransferException("Mainnet is disabled.");
        var st = await Node.GetStatusAsync(ct);
        var id = await TransferService.BroadcastAsync(Node, plan, userConfirmed, ct);
        Tracker.Add(id, plan.To, plan.AmountSompi, plan.FeeSompi, plan.Transaction.Inputs.Select(i => i.PreviousOutpoint), plan.ChangeSompi > 0, st.VirtualDaaScore);
        try { Tracker.Save(HistoryPath); } catch (IOException ex) { WalletLog.Error("Could not save history", ex); }
        WalletLog.Info($"Broadcast tx {id}");
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        Lock();
        if (_node != null) await _node.DisposeAsync();
    }
}
