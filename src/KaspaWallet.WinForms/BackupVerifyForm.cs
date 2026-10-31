using KaspaWallet.Core;

namespace KaspaWallet.WinForms;

/// <summary>KDW-04: the phrase window is closed; the user must type the words at random positions from their written backup.</summary>
internal sealed class BackupVerifyForm : Form
{
    private readonly BackupChallenge _challenge;
    private readonly TextBox[] _boxes;

    public BackupVerifyForm(BackupChallenge challenge)
    {
        _challenge = challenge;
        Text = "Verify your backup"; StartPosition = FormStartPosition.CenterParent;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var g = Theme.Grid(2);
        var intro = Theme.Label("From your WRITTEN backup, enter the requested words. The wallet is not saved until this succeeds.");
        g.Controls.Add(intro); g.SetColumnSpan(intro, 2);
        _boxes = challenge.Positions.Select(_ => new TextBox { Width = 200, ShortcutsEnabled = false }).ToArray();
        for (var i = 0; i < _boxes.Length; i++) { g.Controls.Add(Theme.Label($"Word #{challenge.Positions[i]}")); g.Controls.Add(_boxes[i]); }
        var ok = new Button { Text = "Verify" }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.Add(ok); row.Controls.Add(cancel);
        g.Controls.Add(row); g.SetColumnSpan(row, 2); Controls.Add(g); AcceptButton = ok; CancelButton = cancel;
        Theme.Apply(this);
        ok.Click += (_, _) =>
        {
            if (_challenge.Verify(_boxes.Select(b => b.Text).ToArray())) { DialogResult = DialogResult.OK; return; }
            foreach (var b in _boxes) b.Clear();
            MessageBox.Show(this, "One or more words are wrong. Check your written backup (go back to view the phrase again if needed).",
                "Backup check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        FormClosed += (_, _) => { foreach (var b in _boxes) b.Clear(); };
    }
}
