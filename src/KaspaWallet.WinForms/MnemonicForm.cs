using KaspaWallet.Core;

namespace KaspaWallet.WinForms;

/// <summary>Create: shows a new 24-word phrase once and requires backup acknowledgement. Import: accepts a phrase.</summary>
internal sealed class MnemonicForm : Form
{
    private readonly TextBox _words = new() { Multiline = true, Width = 520, Height = 110, Font = Theme.Mono };
    private readonly CheckBox _ack = new() { Text = "I have written these words down offline. Anyone with them can spend the funds.", AutoSize = true };
    private readonly bool _import;

    public string Mnemonic => _words.Text;

    public MnemonicForm(bool import)
    {
        _import = import;
        Text = import ? "Import wallet" : "Create wallet"; StartPosition = FormStartPosition.CenterParent;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var g = Theme.Grid(1);
        g.Controls.Add(Theme.Label(import ? "Enter your 12/24-word BIP39 mnemonic:" : "Your new recovery phrase (BIP39, 24 words). Path m/44'/111111'/0'/0/0."));
        g.Controls.Add(_words);
        if (!import)
        {
            _words.Text = MnemonicService.Generate(24);
            _words.ReadOnly = true;
            _words.ShortcutsEnabled = false; // discourage clipboard copies of the phrase
            g.Controls.Add(_ack);
        }
        var ok = new Button { Text = "Continue" }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.Add(ok); row.Controls.Add(cancel);
        g.Controls.Add(row); Controls.Add(g); CancelButton = cancel;
        Theme.Apply(this);
        ok.Click += (_, _) =>
        {
            if (!_import && !_ack.Checked) { MessageBox.Show(this, "Please confirm you backed up the phrase."); return; }
            if (!MnemonicService.IsValid(_words.Text)) { MessageBox.Show(this, "Invalid mnemonic (word list or checksum)."); return; }
            DialogResult = DialogResult.OK;
        };
    }

    public void Clear() => _words.Clear();
}
