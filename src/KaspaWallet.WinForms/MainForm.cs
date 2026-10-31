using KaspaWallet.Core;
using KaspaWallet.Core.Transfer;

namespace KaspaWallet.WinForms;

/// <summary>Main view: header, address + copy, balance, send form, status bar.</summary>
internal sealed class MainForm : Form
{
    private readonly WalletManager _mgr;
    private readonly TextBox _address = new() { ReadOnly = true, Width = 620, Font = Theme.Mono };
    private readonly Label _balance = Theme.Label("-- KAS", Theme.Title);
    private readonly Label _utxos = Theme.Label("");
    private readonly TextBox _to = new() { Width = 620, Font = Theme.Mono };
    private readonly TextBox _amount = new() { Width = 200 };
    private readonly ComboBox _fee = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Button _send = new() { Text = "Review & send…" };
    private readonly Button _refresh = new() { Text = "Refresh" };
    private readonly Button _connect = new() { Text = "Connect" };
    private readonly Button _lock = new() { Text = "Lock" };
    private readonly ToolStripStatusLabel _status = new() { Text = "Not connected", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _net = new();

    public MainForm(WalletManager mgr)
    {
        _mgr = mgr;
        Text = $"Kaspa Desktop Wallet — {mgr.Settings.Network}"; StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 480);
        _address.Text = mgr.Address;
        _fee.Items.AddRange(new object[] { FeeMode.MinimumRelay, FeeMode.NodeEstimate }); _fee.SelectedIndex = 0;
        _net.Text = $"{mgr.Settings.Network} | {mgr.Settings.EffectiveWrpcUrl}";

        var g = Theme.Grid(2);
        g.Controls.Add(Theme.Label("Receive address")); var copy = new Button { Text = "Copy" };
        var addrRow = new FlowLayoutPanel { AutoSize = true }; addrRow.Controls.Add(_address); addrRow.Controls.Add(copy); g.Controls.Add(addrRow);
        g.Controls.Add(Theme.Label("Balance")); var balRow = new FlowLayoutPanel { AutoSize = true }; balRow.Controls.Add(_balance); balRow.Controls.Add(_utxos); g.Controls.Add(balRow);
        g.Controls.Add(Theme.Label("Send to")); g.Controls.Add(_to);
        g.Controls.Add(Theme.Label("Amount (KAS)")); g.Controls.Add(_amount);
        g.Controls.Add(Theme.Label("Fee")); g.Controls.Add(_fee);
        var actions = new FlowLayoutPanel { AutoSize = true }; actions.Controls.AddRange(new Control[] { _send, _refresh, _connect, _lock });
        g.Controls.Add(new Label()); g.Controls.Add(actions);
        var strip = new StatusStrip(); strip.Items.Add(_status); strip.Items.Add(_net);
        Controls.Add(g); Controls.Add(strip);
        Theme.Apply(this); strip.BackColor = Theme.Panel; strip.ForeColor = Theme.Fore;

        copy.Click += (_, _) => { SecureClipboard.Set(_address.Text, mgr.Settings.ClipboardClearSeconds); SetStatus($"Address copied (clears in {mgr.Settings.ClipboardClearSeconds}s)."); };
        g.Controls.Add(Theme.Label("History (local)")); g.Controls.Add(_history);
        _idle.Tick += (_, _) => { if (_mgr.IsUnlocked && DateTime.UtcNow - _lastInput > TimeSpan.FromMinutes(_mgr.Settings.AutoLockMinutes)) DoLock("Auto-locked after inactivity."); };
        _idle.Start();
        KeyPreview = true; KeyDown += (_, _) => _lastInput = DateTime.UtcNow; MouseMove += (_, _) => _lastInput = DateTime.UtcNow;
        Application.AddMessageFilter(new InputFilter(() => _lastInput = DateTime.UtcNow));
        _connect.Click += async (_, _) => await ConnectAsync();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _send.Click += async (_, _) => await SendAsync();
        _lock.Click += (_, _) => DoLock("Locked. Restart to unlock.");
        Shown += async (_, _) => await ConnectAsync();
        FormClosed += async (_, _) => await _mgr.DisposeAsync();
    }

    private readonly ListBox _history = new() { Width = 640, Height = 120, AccessibleName = "Transaction history" };
    private readonly System.Windows.Forms.Timer _idle = new() { Interval = 15_000 };
    private DateTime _lastInput = DateTime.UtcNow;

    private void DoLock(string msg) { _mgr.Lock(); _send.Enabled = false; SetStatus(msg); }

    private sealed class InputFilter(Action touch) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m) { if (m.Msg is 0x0100 or 0x0200 or 0x0201) touch(); return false; }
    }

    private async Task UpdateHistoryAsync(CancellationToken ct)
    {
        var items = await _mgr.RecheckHistoryAsync(ct);
        _history.Items.Clear();
        foreach (var t in items.Reverse())
            _history.Items.Add($"{t.SubmittedAt:yyyy-MM-dd HH:mm} OUT {Kas.Format(t.AmountSompi)} KAS fee {Kas.Format(t.FeeSompi)}  {t.State} ({t.Confirmations} DAA)  {t.TxId}");
    }

    private void SetStatus(string s) => _status.Text = $"{DateTime.Now:HH:mm:ss}  {s}";

    private async Task Busy(Func<Task> work)
    {
        UseWaitCursor = true; Enabled = false;
        try { await work(); }
        catch (Exception ex) { SetStatus("Error: " + ex.Message); }
        finally { UseWaitCursor = false; Enabled = true; _send.Enabled = _mgr.IsUnlocked; }
    }

    private Task ConnectAsync() => Busy(async () =>
    {
        SetStatus("Connecting…");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var st = await _mgr.ConnectAsync(cts.Token);
        var expected = _mgr.Settings.Network.ExpectedNodeNetworkId();
        SetStatus($"Connected: {st.ServerVersion} {st.NetworkId} synced={st.IsSynced} utxoindex={st.HasUtxoIndex}" +
                  (string.Equals(st.NetworkId, expected, StringComparison.OrdinalIgnoreCase) ? "" : $"  ⚠ expected {expected}"));
        await RefreshCore(cts.Token);
    });

    private Task RefreshAsync() => Busy(async () =>
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await RefreshCore(cts.Token);
    });

    private async Task RefreshCore(CancellationToken ct)
    {
        var (bal, n) = await _mgr.RefreshAsync(ct);
        _balance.Text = $"{Kas.Format(bal)} KAS"; _utxos.Text = $"({n} UTXO)";
        SetStatus($"Balance updated ({n} UTXO).");
        await UpdateHistoryAsync(ct);
    }

    private Task SendAsync() => Busy(async () =>
    {
        ulong sompi;
        try { sompi = Kas.ParseKas(_amount.Text); } catch (FormatException ex) { SetStatus(ex.Message); return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var plan = await _mgr.PrepareTransferAsync(_to.Text, sompi, (FeeMode)_fee.SelectedItem!, cts.Token);
        var answer = MessageBox.Show(this, plan.Summary() + "\n\nBroadcast this transaction? This cannot be undone.",
            "Confirm send", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) { SetStatus("Send cancelled."); return; }
        if (plan.FoldedChangeSompi > 0 && MessageBox.Show(this, string.Join("\n", plan.Warnings) + "\n\nSend anyway and lose this change to the fee?",
                "Small change goes to fee", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        { SetStatus("Send cancelled (change would go to fee)."); return; }
        if (plan.Network == WalletNetwork.Mainnet && !MainnetSendWarning.Confirm(this, plan)) { SetStatus("Mainnet send cancelled."); return; }
        var id = await _mgr.BroadcastAsync(plan, userConfirmed: true, cts.Token);
        SetStatus($"Submitted tx {id}");
        _amount.Clear();
        await RefreshCore(cts.Token);
    });
}
