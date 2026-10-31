using System.Text.Json;

namespace KaspaWallet.Core.Keystore;

public sealed class UnlockThrottledException : InvalidOperationException
{
    public UnlockThrottledException(TimeSpan wait) : base($"Too many wrong passwords. Please wait {Math.Ceiling(wait.TotalSeconds):0} s and try again.") => Wait = wait;
    public TimeSpan Wait { get; }
}

/// <summary>Persisted failed-unlock counter (survives restarts). Backoff after 3 failures: 2,4,8,16,30,30… s.</summary>
public sealed class UnlockThrottle
{
    public int Failures { get; set; }
    public DateTimeOffset LastFailureUtc { get; set; }

    public TimeSpan Delay => Failures > 2 ? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(5, Failures - 2))) : TimeSpan.Zero;

    public TimeSpan Remaining(DateTimeOffset now)
    {
        var r = LastFailureUtc + Delay - now;
        // A clock moved backwards must not lock the user out forever: cap at Delay.
        return r <= TimeSpan.Zero ? TimeSpan.Zero : (r > Delay ? Delay : r);
    }

    public void RecordFailure(DateTimeOffset now) { Failures = Math.Min(Failures + 1, 1000); LastFailureUtc = now; }

    public static UnlockThrottle Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UnlockThrottle>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return new UnlockThrottle { Failures = 3, LastFailureUtc = DateTimeOffset.UtcNow }; } // tampered state: fail safe (short wait)
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            WalletKeystore.WriteOwnerOnly(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WalletLog.Error("Could not save unlock state", ex); }
    }

    public static void Reset(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
