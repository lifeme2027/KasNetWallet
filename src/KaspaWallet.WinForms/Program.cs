using KaspaWallet.Core;

namespace KaspaWallet.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var dir = WalletSettings.DefaultDirectory;
        var settingsPath = Path.Combine(dir, "settings.json");
        var settings = WalletSettings.Load(settingsPath);
        using var start = new StartForm(settings, settingsPath);
        if (start.ShowDialog() != DialogResult.OK || start.Manager is null) return;
        Application.Run(new MainForm(start.Manager));
    }
}
