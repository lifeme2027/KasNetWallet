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

    /// <summary>Back-compat overload (no notice).</summary>
    public static UnlockThrottle Load(string path) => Load(path, DateTimeOffset.UtcNow, out _);

    /// <summary>
    /// N1: never locks permanently. Unreadable/corrupt state → reset to zero failures (file removed) with a notice.
    /// Timestamp in the future (clock change / tampering) → clamped to now and re-saved, with a notice.
    /// Out-of-range failure counts are clamped to 0..1000.
    /// </summary>
    public static UnlockThrottle Load(string path, DateTimeOffset now, out string? notice)
    {
        notice = null;
        if (!File.Exists(path)) return new();
        UnlockThrottle? st;
        try { st = JsonSerializer.Deserialize<UnlockThrottle>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { st = null; }
        if (st is null)
        {
            Reset(path);
            notice = "The saved unlock-attempt counter was unreadable and has been reset.";
            WalletLog.Warn("unlock-state file unreadable; reset.");
            return new();
        }
        var changed = false;
        if (st.Failures < 0 || st.Failures > 1000) { st.Failures = Math.Clamp(st.Failures, 0, 1000); changed = true; }
        if (st.LastFailureUtc > now + TimeSpan.FromMinutes(1))
        {
            st.LastFailureUtc = now - st.Delay; // clamp: no remaining wait from a bogus future time
            changed = true;
            notice = "The saved unlock-attempt time was in the future (system clock changed?) and has been corrected.";
            WalletLog.Warn("unlock-state timestamp in the future; clamped.");
        }
        if (changed) st.Save(path);
        return st;
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
