using KasNetWallet.Core.Keystore;
using KasNetWallet.Core.Rpc;
using KasNetWallet.Core.Transfer;
using KasNetWallet.Core.Tracking;
using System.Text;

namespace KasNetWallet.Core;

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
    public string? Address => _account?.Address ?? (TryLoadKeystore(out var f, out _) ? f!.Address : null);

    /// <summary>Never throws: returns false with a user-safe error when the wallet file is missing/corrupt (KDW-05).</summary>
    public bool TryLoadKeystore(out KeystoreFile? file, out string? error)
    {
        file = null; error = null;
        if (!KeystoreExists) { error = "No wallet file."; return false; }
        try { file = WalletKeystore.Load(KeystorePath); return true; }
        catch (KeystoreCorruptException ex) { error = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "Cannot read the wallet file (" + ex.GetType().Name + "). Check file permissions or restore it from backup."; }
        return false;
    }

    /// <summary>Loads the keystore or throws a single (non-nested) user-presentable exception (N5).</summary>
    private KeystoreFile LoadKeystoreOrThrow()
    {
        if (!KeystoreExists) throw new FileNotFoundException("No wallet file.");
        try { return WalletKeystore.Load(KeystorePath); }
        catch (KeystoreCorruptException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException("Cannot read the wallet file (" + ex.GetType().Name + "). Check file permissions or restore it from backup.", ex); }
    }

    /// <summary>Non-null when the last Unlock() had to auto-recover the throttle state (N1). UI shows it once.</summary>
    public string? ThrottleNotice { get; private set; }

    /// <summary>Per-process copy of the throttle state, so a non-writable state file cannot disable throttling (R3-1).</summary>
    private static readonly Dictionary<string, UnlockThrottle> MemoryThrottle = new(StringComparer.Ordinal);

    /// <summary>
    /// NEW wallet: requires a passed <see cref="BackupChallenge"/> for this exact mnemonic before anything is written (KDW-04).
    /// </summary>
    public void CreateNew(string mnemonic, string password, BackupChallenge backup)
    {
        if (backup is null || !backup.IsVerified) throw new InvalidOperationException("Recovery phrase backup has not been verified.");
        if (!backup.Matches(mnemonic)) throw new InvalidOperationException("Backup verification belongs to a different recovery phrase.");
        CreateOrImport(mnemonic, password);
    }

    /// <summary>Import (user already owns the phrase), or internal use by <see cref="CreateNew"/>. Refuses to overwrite.</summary>
    public void CreateOrImport(string mnemonic, string password)
    {
        if (KeystoreExists) throw new InvalidOperationException("A wallet already exists for this network. Remove/back up the file first.");
        password = NormalizePassword(password);
        var file = WalletKeystore.Encrypt(mnemonic, password, Settings.Network, Kdf);
        WalletKeystore.Save(file, KeystorePath);
    }

    /// <summary>
    /// Throws only user-presentable exceptions: <see cref="ArgumentException"/> (empty password), <see cref="UnlockThrottledException"/>,
    /// <see cref="WrongPasswordException"/>, <see cref="KeystoreCorruptException"/>. Failed attempts persist across restarts (KDW-09).
    /// </summary>
    public void Unlock(string password)
    {
        Lock();
        var state = UnlockThrottle.Load(ThrottlePath, DateTimeOffset.UtcNow, out var notice);
        // R3-1: an in-process record (kept when the file could not be written) wins if it is stricter.
        lock (MemoryThrottle)
            if (MemoryThrottle.TryGetValue(ThrottlePath, out var mem) && mem.Remaining(DateTimeOffset.UtcNow) >= state.Remaining(DateTimeOffset.UtcNow))
                state = new UnlockThrottle { Failures = mem.Failures, LastFailureUtc = mem.LastFailureUtc };
        ThrottleNotice = notice;
        var remaining = state.Remaining(DateTimeOffset.UtcNow);
        if (remaining > TimeSpan.Zero) throw new UnlockThrottledException(remaining);
        var pw = NormalizePassword(password, checkStrength: false);
        var file = LoadKeystoreOrThrow();
        try
        {
            _account = WalletKeystore.Unlock(file, pw);
            UnlockThrottle.Reset(ThrottlePath);
            lock (MemoryThrottle) MemoryThrottle.Remove(ThrottlePath);
        }
        catch (WrongPasswordException)
        {
            state.RecordFailure(DateTimeOffset.UtcNow);
            var saved = state.Save(ThrottlePath);
            if (saved) { lock (MemoryThrottle) MemoryThrottle.Remove(ThrottlePath); }
            else
            {
                // R3-1 fail closed: strictest delay, kept in memory for this process, and tell the user.
                state.Failures = UnlockThrottle.StrictFailures;
                ThrottleNotice = "The unlock-attempt counter could not be saved (wallet folder not writable?). The strictest delay (30 s) applies to every attempt until it can be saved.";
            }
            if (!saved) lock (MemoryThrottle) MemoryThrottle[ThrottlePath] = new UnlockThrottle { Failures = state.Failures, LastFailureUtc = state.LastFailureUtc };
            throw;
        }
    }

    public string ThrottlePath => Path.Combine(Directory, $"unlock-state.{Settings.Network.ToString().ToLowerInvariant()}.json");

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
        var file = LoadKeystoreOrThrow();
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
        return TransferService.Build(Account, coins, to, amountSompi, mode, rate, Settings.FeePolicy, await CurrentMassPolicyAsync(ct));
    }

    /// <summary>Settings mass policy plus the node's virtual DAA score (NKaspa Network.MassPolicyAt picks pre/post-Toccata rules).</summary>
    private async Task<MassPolicy> CurrentMassPolicyAsync(CancellationToken ct)
    {
        ulong? daa = null;
        try { daa = (await Node.GetStatusAsync(ct)).VirtualDaaScore; }
        catch (Exception ex) when (ex is not OperationCanceledException) { WalletLog.Warn("DAA score unavailable; using the default mass policy."); }
        return Settings.MassPolicyFor(Settings.Network).WithDaaScore(daa);
    }

    /// <summary>N2: Send max — amount = spendable balance − fee, no change output.</summary>
    public async Task<TransferPlan> PrepareSendMaxAsync(string to, FeeMode mode, CancellationToken ct = default)
    {
        var coins = await Node.GetCoinsAsync(Account.Address, ct);
        double? rate = mode == FeeMode.NodeEstimate ? await Node.GetNormalFeeRateAsync(ct) : null;
        return TransferService.BuildSendMax(Account, coins, to, mode, rate, Settings.FeePolicy, await CurrentMassPolicyAsync(ct));
    }

    public async Task<string> BroadcastAsync(TransferPlan plan, bool userConfirmed, CancellationToken ct = default, bool warningsAcknowledged = false)
    {
        if (plan.Network == WalletNetwork.Mainnet && !Settings.MainnetEnabled) throw new TransferException("Mainnet is disabled.");
        var st = await Node.GetStatusAsync(ct);
        string id;
        try { id = await TransferService.BroadcastAsync(Node, plan, userConfirmed, ct, warningsAcknowledged); }
        catch (TransferStatusUnknownException ex)
        {
            // Track it anyway so Recheck can resolve Accepted/Dropped.
            Tracker.Add(ex.TransactionId, plan.To, plan.AmountSompi, plan.FeeSompi, plan.Transaction.Inputs.Select(i => i.PreviousOutpoint), plan.ChangeSompi > 0, st.VirtualDaaScore);
            try { Tracker.Save(HistoryPath); } catch (IOException e2) { WalletLog.Error("Could not save history", e2); }
            throw;
        }
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
