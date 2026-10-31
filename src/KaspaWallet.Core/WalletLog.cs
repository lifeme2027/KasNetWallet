namespace KaspaWallet.Core;

/// <summary>
/// Minimal local file log. Every message passes through <see cref="SecretRedactor"/>. No remote sink, no telemetry.
/// Location: %LOCALAPPDATA%/KaspaDesktopWallet/logs/wallet-YYYYMMDD.log.
/// </summary>
public static class WalletLog
{
    private static readonly object Gate = new();
    public static string? Directory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaspaDesktopWallet", "logs");

    /// <summary>For tests: capture redacted lines instead of writing a file.</summary>
    public static Action<string>? Sink { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}"); // no stack/ToString of objects

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {level} {SecretRedactor.Redact(message)}";
        if (Sink is not null) { Sink(line); return; }
        if (Directory is null) return;
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, $"wallet-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch (IOException) { /* logging must never break the wallet */ }
    }
}
