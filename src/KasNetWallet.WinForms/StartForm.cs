using KasNetWallet.Core;
using KasNetWallet.Core.Keystore;

namespace KasNetWallet.WinForms;

/// <summary>Entry flow: choose network, then Unlock / Create / Import.</summary>
internal sealed class StartForm : Form
{
    private readonly WalletSettings _settings;
    private readonly string _settingsPath;
    private readonly ComboBox _network = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _url = new() { Width = 420 };
    private readonly Label _info = Theme.Label("");
    private readonly Button _unlock = new() { Text = "Unlock wallet" };
    private readonly Button _create = new() { Text = "Create new wallet" };
    private readonly Button _import = new() { Text = "Import from mnemonic" };

    public WalletManager? Manager { get; private set; }

    public StartForm(WalletSettings settings, string settingsPath)
    {
        _settings = settings; _settingsPath = settingsPath;
        Text = "KasNet Wallet"; StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;

        _network.Items.AddRange(Enum.GetNames<WalletNetwork>());
        _network.SelectedItem = settings.Network.ToString();
        _url.Text = settings.WrpcUrl ?? "";
        _url.PlaceholderText = settings.Network.DefaultWrpcUrl();
        _network.SelectedIndexChanged += (_, _) => Refresh_();

        var g = Theme.Grid(2);
        g.Controls.Add(Theme.Label("KasNet Wallet (MVP, alpha)", Theme.Title)); g.SetColumnSpan(g.Controls[^1], 2);
        g.Controls.Add(Theme.Label("Network")); g.Controls.Add(_network);
        g.Controls.Add(Theme.Label("wRPC URL (JSON)")); g.Controls.Add(_url);
        g.Controls.Add(_info); g.SetColumnSpan(_info, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange(new Control[] { _unlock, _create, _import });
        g.Controls.Add(buttons); g.SetColumnSpan(buttons, 2);
        Controls.Add(g);
        Theme.Apply(this);

        _unlock.Click += (_, _) => DoUnlock();
        _create.Click += (_, _) => DoCreate(import: false);
        _import.Click += (_, _) => DoCreate(import: true);
        Refresh_();
    }

    private WalletManager BuildManager()
    {
        var net = Enum.Parse<WalletNetwork>((string)_network.SelectedItem!);
        if (net == WalletNetwork.Mainnet && !_settings.MainnetEnabled)
        {
            var ok = MessageBox.Show(this,
                "MAINNET uses real KAS. This is an unaudited alpha wallet built on an alpha library (NKaspa 0.1.9-alpha).\n" +
                "Losses caused by bugs cannot be reversed. Only continue if you accept this risk.\n\nEnable mainnet?",
                "Enable mainnet", MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2);
            if (ok != DialogResult.Yes) throw new OperationCanceledException("Mainnet not enabled.");
            _settings.MainnetEnabled = true;
        }
        _settings.Network = net;
        _settings.WrpcUrl = string.IsNullOrWhiteSpace(_url.Text) ? null : _url.Text.Trim();
        _settings.Save(_settingsPath);
        return new WalletManager(WalletSettings.DefaultDirectory, _settings);
    }

    private void Refresh_()
    {
        var net = Enum.Parse<WalletNetwork>((string)_network.SelectedItem!);
        _url.PlaceholderText = net.DefaultWrpcUrl();
        var probe = new WalletManager(WalletSettings.DefaultDirectory, new WalletSettings { Network = net });
        _unlock.Enabled = probe.KeystoreExists;
        _create.Enabled = _import.Enabled = !probe.KeystoreExists;
        if (!probe.KeystoreExists) _info.Text = "No wallet for this network yet.";
        else if (probe.TryLoadKeystore(out var f, out var err)) _info.Text = $"Wallet found: {f!.Address}";
        else _info.Text = "Wallet file problem: " + err + $"\nFile: {probe.KeystorePath}";
    }

    private void ShowThrottleNotice(WalletManager mgr)
    {
        if (mgr.ThrottleNotice is string n)
            MessageBox.Show(this, n + " You can continue normally.", "Unlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void DoUnlock()
    {
        WalletManager mgr;
        try { mgr = BuildManager(); } catch (OperationCanceledException) { return; }
        using var dlg = new PasswordForm("Unlock wallet", confirm: false);
        while (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                UseWaitCursor = true;
                try { mgr.Unlock(dlg.Password); } finally { ShowThrottleNotice(mgr); }
                Manager = mgr; DialogResult = DialogResult.OK; return;
            }
            catch (WrongPasswordException) { MessageBox.Show(this, "Wrong password.", "Unlock", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (UnlockThrottledException ex) { MessageBox.Show(this, ex.Message, "Unlock", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Unlock", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (IOException ex) { MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Unlock", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            catch (Exception ex) { ErrorReporter.Show(this, "Unlock failed", ex); return; }
            finally { UseWaitCursor = false; dlg.Clear(); }
        }
    }

    private void DoCreate(bool import)
    {
        WalletManager mgr;
        try { mgr = BuildManager(); } catch (OperationCanceledException) { return; }
        using var form = new MnemonicForm(import);
        BackupChallenge? backup = null;
        while (true)
        {
            if (form.ShowDialog(this) != DialogResult.OK) { form.Clear(); return; }
            if (import) break;
            // KDW-04: spot-check 3 random words before anything is written to disk.
            backup = new BackupChallenge(form.Mnemonic, 3);
            using var verify = new BackupVerifyForm(backup);
            if (verify.ShowDialog(this) == DialogResult.OK && backup.IsVerified) break;
        }
        using var pw = new PasswordForm("Set wallet password", confirm: true);
        if (pw.ShowDialog(this) != DialogResult.OK) { form.Clear(); return; }
        try
        {
            UseWaitCursor = true;
            if (import) mgr.CreateOrImport(form.Mnemonic, pw.Password);
            else mgr.CreateNew(form.Mnemonic, pw.Password, backup!);
            mgr.Unlock(pw.Password);
            Manager = mgr; DialogResult = DialogResult.OK;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or KeystoreCorruptException)
        {
            MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Wallet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { ErrorReporter.Show(this, "Could not create the wallet", ex); }
        finally { UseWaitCursor = false; form.Clear(); pw.Clear(); }
    }
}
