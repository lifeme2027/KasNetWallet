namespace KaspaWallet.WinForms;

internal sealed class PasswordForm : Form
{
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true, Width = 300 };
    private readonly TextBox _pw2 = new() { UseSystemPasswordChar = true, Width = 300 };
    private readonly bool _confirm;

    public string Password => _pw.Text;

    public PasswordForm(string title, bool confirm)
    {
        _confirm = confirm;
        Text = title; StartPosition = FormStartPosition.CenterParent; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        var g = Theme.Grid(2);
        g.Controls.Add(Theme.Label("Password")); g.Controls.Add(_pw);
        if (confirm) { g.Controls.Add(Theme.Label("Repeat password")); g.Controls.Add(_pw2); }
        var ok = new Button { Text = "OK" }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.Add(ok); row.Controls.Add(cancel);
        g.Controls.Add(row); g.SetColumnSpan(row, 2);
        Controls.Add(g); AcceptButton = ok; CancelButton = cancel;
        Theme.Apply(this);
        ok.Click += (_, _) =>
        {
            if (_confirm && _pw.Text.Length < 8) { MessageBox.Show(this, "Use at least 8 characters."); return; }
            if (_confirm && _pw.Text != _pw2.Text) { MessageBox.Show(this, "Passwords do not match."); return; }
            DialogResult = DialogResult.OK;
        };
    }

    public void Clear() { _pw.Clear(); _pw2.Clear(); }
}
