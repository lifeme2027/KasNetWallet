using KasNetWallet.Core.Transfer;

namespace KasNetWallet.WinForms;

/// <summary>Separate, second dialog for every mainnet send: user must type the exact phrase before Send enables.</summary>
internal static class MainnetSendWarning
{
    public const string Phrase = "SEND MAINNET";

    public static bool Confirm(IWin32Window owner, TransferPlan plan)
    {
        using var f = new Form { Text = "⚠ MAINNET — real funds", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.DarkRed, ForeColor = Color.White };
        var lbl = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11, FontStyle.Bold),
            Text = $"You are about to send {Kas.Format(plan.AmountSompi)} KAS (+{Kas.Format(plan.FeeSompi)} fee) on MAINNET to:\n{plan.To}\n\n" +
                   "This is irreversible. Check every character of the address. This software is alpha and unaudited.\n\n" +
                   $"Type {Phrase} to continue:" };
        var box = new TextBox { Width = 300, AccessibleName = "Confirmation phrase" };
        var ok = new Button { Text = "Send on mainnet", Enabled = false, DialogResult = DialogResult.OK, BackColor = Color.White, ForeColor = Color.DarkRed };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, BackColor = Color.White, ForeColor = Color.Black };
        box.TextChanged += (_, _) => ok.Enabled = box.Text == Phrase;
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16) };
        var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.Add(cancel); row.Controls.Add(ok);
        p.Controls.AddRange(new Control[] { lbl, box, row });
        f.Controls.Add(p); f.AcceptButton = null; f.CancelButton = cancel; f.ActiveControl = cancel;
        return f.ShowDialog(owner) == DialogResult.OK && box.Text == Phrase;
    }
}
