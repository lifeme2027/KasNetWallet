namespace KaspaWallet.Core.Keystore;

public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>Default for real wallets: Argon2id, 64 MiB, t=3, p=4 (OWASP-style baseline).</summary>
    public static KdfParameters Default { get; } = new(64 * 1024, 3, 4);

    /// <summary>Fast parameters for unit tests only.</summary>
    public static KdfParameters TestOnly { get; } = new(1024, 1, 1);

    internal void Validate()
    {
        if (MemoryKiB < 1024 || MemoryKiB > 4 * 1024 * 1024) throw new InvalidDataException("Argon2 memory out of range.");
        if (Iterations < 1 || Iterations > 100) throw new InvalidDataException("Argon2 iterations out of range.");
        if (Parallelism < 1 || Parallelism > 64) throw new InvalidDataException("Argon2 parallelism out of range.");
    }
}
