using KaspaWallet.Core;

namespace KaspaWallet.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // KDW-05: no unhandled-exception dialog with stack traces; log redacted type+message only.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorReporter.Show(null, "Unexpected error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorReporter.Log("Fatal unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { ErrorReporter.Log("Unobserved task exception", e.Exception); e.SetObserved(); };
        ApplicationConfiguration.Initialize();
        var dir = WalletSettings.DefaultDirectory;
        var settingsPath = Path.Combine(dir, "settings.json");
        var settings = WalletSettings.Load(settingsPath, out var notices);
        if (notices.Count > 0)
            MessageBox.Show(SecretRedactor.Redact("Some settings were invalid and have been corrected:\n\n" + string.Join("\n", notices)),
                "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
        using var start = new StartForm(settings, settingsPath);
        if (start.ShowDialog() != DialogResult.OK || start.Manager is null) return;
        Application.Run(new MainForm(start.Manager));
    }
}
