using System.Security.Cryptography;

namespace KasNetWallet.Core;

/// <summary>
/// Mnemonic backup verification (KDW-04): the user must re-enter N randomly chosen words (CSPRNG positions)
/// before a NEW wallet file may be written. Never exposes the words; ToString is redacted.
/// </summary>
public sealed class BackupChallenge
{
    private readonly string[] _words;
    private readonly byte[] _fingerprint;

    public BackupChallenge(string mnemonic, int count = 3)
    {
        var normalized = MnemonicService.Normalize(mnemonic);
        _words = normalized.Split(' ');
        if (count < 1 || count > _words.Length) throw new ArgumentOutOfRangeException(nameof(count));
        var picks = new SortedSet<int>();
        while (picks.Count < count) picks.Add(RandomNumberGenerator.GetInt32(_words.Length) + 1);
        Positions = picks.ToArray();
        _fingerprint = Fingerprint(normalized);
    }

    /// <summary>1-based word positions the user must type.</summary>
    public IReadOnlyList<int> Positions { get; }
    public bool IsVerified { get; private set; }
    public int FailedAttempts { get; private set; }

    public bool Verify(IReadOnlyList<string> answers)
    {
        if (answers is null || answers.Count != Positions.Count) { FailedAttempts++; return false; }
        var ok = true;
        for (var i = 0; i < Positions.Count; i++)
            ok &= string.Equals((answers[i] ?? "").Normalize(System.Text.NormalizationForm.FormKC).Trim().ToLowerInvariant(),
                                _words[Positions[i] - 1], StringComparison.Ordinal);
        if (ok) IsVerified = true; else FailedAttempts++;
        return ok;
    }

    internal bool Matches(string mnemonic) =>
        CryptographicOperations.FixedTimeEquals(_fingerprint, Fingerprint(MnemonicService.Normalize(mnemonic)));

    private static byte[] Fingerprint(string normalized) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("kdw-backup|" + normalized));

    public override string ToString() => $"BackupChallenge(positions={string.Join(',', Positions)}, verified={IsVerified})";
}
