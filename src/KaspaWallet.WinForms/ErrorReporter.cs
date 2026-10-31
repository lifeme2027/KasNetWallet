using KaspaWallet.Core;

namespace KaspaWallet.WinForms;

/// <summary>User-facing error box + redacted log. Never shows stack traces; messages pass through SecretRedactor.</summary>
internal static class ErrorReporter
{
    public static void Log(string context, Exception? ex)
    {
        try { if (ex != null) WalletLog.Error(context, ex); else WalletLog.Warn(context); } catch { /* logging must never crash the app */ }
    }

    public static void Show(IWin32Window? owner, string context, Exception ex)
    {
        Log(context, ex);
        var msg = SecretRedactor.Redact(ex is AggregateException ae && ae.InnerException != null ? ae.InnerException.Message : ex.Message);
        try
        {
            MessageBox.Show(owner, $"{context}:\n{msg}\n\nThe wallet file was not changed. If this repeats, restart the app and check the log folder.",
                "Kaspa Desktop Wallet", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
