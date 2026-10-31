namespace KasNetWallet.WinForms;

/// <summary>Dark theme. Original code.</summary>
internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(24, 26, 27);
    public static readonly Color Panel = Color.FromArgb(40, 44, 46);
    public static readonly Color Accent = Color.FromArgb(73, 234, 203); // Kaspa teal
    public static readonly Color Fore = Color.Gainsboro;
    public static readonly Font Body = new("Segoe UI", 10F);
    public static readonly Font Title = new("Segoe UI Semibold", 14F, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 10F);

    public static void Apply(Control root)
    {
        root.BackColor = Back; root.ForeColor = Fore; root.Font = Body;
        foreach (Control c in root.Controls) Style(c);
    }

    private static void Style(Control c)
    {
        switch (c)
        {
            case Button b:
                b.FlatStyle = FlatStyle.Flat; b.BackColor = Panel; b.ForeColor = Accent;
                b.FlatAppearance.BorderColor = Accent; b.AutoSize = true; b.Padding = new Padding(6, 2, 6, 2);
                break;
            case TextBox t:
                t.BackColor = Panel; t.ForeColor = Fore; t.BorderStyle = BorderStyle.FixedSingle;
                break;
        }
        foreach (Control child in c.Controls) Style(child);
    }

    public static Label Label(string text, Font? font = null) => new() { Text = text, AutoSize = true, Font = font ?? Body, Margin = new Padding(3, 8, 3, 3) };

    public static TableLayoutPanel Grid(int cols) => new()
    {
        Dock = DockStyle.Fill, ColumnCount = cols, AutoSize = true, Padding = new Padding(16),
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
    };
}
