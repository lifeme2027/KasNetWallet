using System.Text.RegularExpressions;
using NKaspa;

namespace KaspaWallet.Core;

/// <summary>
/// Last-line defense for logs and error text: masks extended keys (xprv/kprv/ktrv/tprv…), 64-hex strings
/// (raw private keys / seeds) and runs of ≥12 consecutive BIP39 words (mnemonics). Addresses and tx ids are left
/// readable only when they are not 64-hex runs adjacent to key prefixes; tx ids (64 hex) are masked too, by design.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "[REDACTED]";
    private static readonly HashSet<string> Words = LoadWords();

    [GeneratedRegex(@"\b[a-z]{1,2}(prv|tprv|trv)[1-9A-HJ-NP-Za-km-z]{50,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExtPriv();

    [GeneratedRegex(@"\b(xprv|tprv|kprv|ktrv)[1-9A-HJ-NP-Za-km-z]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExtPrivNamed();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{64,}(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();

    [GeneratedRegex(@"[A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = ExtPrivNamed().Replace(text, Mask);
        s = ExtPriv().Replace(s, Mask);
        s = LongHex().Replace(s, Mask);
        s = RedactBase64(s);
        return RedactMnemonicRuns(s);
    }

    private static string RedactMnemonicRuns(string s)
    {
        var matches = Word().Matches(s);
        var result = new System.Text.StringBuilder(s);
        int runStart = -1, runCount = 0; var structured = false;
        var spans = new List<(int start, int end)>();
        for (var i = 0; i <= matches.Count; i++)
        {
            var isWord = i < matches.Count && Words.Contains(matches[i].Value.ToLowerInvariant());
            if (isWord && runCount > 0)
            {
                // separators: whitespace, or list/JSON/numbering punctuation and digits ("1. a, 2. b", ["a","b"], a-b)
                var prev = matches[i - 1];
                var gap = s.AsSpan(prev.Index + prev.Length, matches[i].Index - prev.Index - prev.Length);
                if (!IsSeparator(gap)) { Flush(i); runStart = i; runCount = 1; structured = false; continue; }
                if (!gap.IsWhiteSpace()) structured = true;
            }
            if (isWord) { if (runCount == 0) runStart = i; runCount++; }
            else Flush(i);
        }
        for (var k = spans.Count - 1; k >= 0; k--)
            result.Remove(spans[k].start, spans[k].end - spans[k].start).Insert(spans[k].start, Mask);
        return result.ToString();

        void Flush(int i)
        {
            if (runCount >= (structured ? StructuredThreshold : 12))
            {
                var first = matches[runStart]; var last = matches[runStart + runCount - 1];
                spans.Add((first.Index, last.Index + last.Length));
            }
            runCount = 0; runStart = -1; structured = false;
        }
    }

    /// <summary>Runs using list punctuation/numbering are masked from this many words (KDW-10).</summary>
    public const int StructuredThreshold = 8;
    public const int MaxGap = 48;

    private static bool IsSeparator(ReadOnlySpan<char> gap)
    {
        // N7: wide separators (padding, tabs, table borders, bullets) up to 48 chars; still only separator characters.
        if (gap.Length == 0 || gap.Length > MaxGap) return false;
        foreach (var c in gap)
            if (!(char.IsWhiteSpace(c) || char.IsDigit(c) || char.IsControl(c) || ",.-_;:|/\\\"'`[](){}<>*#~+=•·–—>".Contains(c))) return false;
        return true;
    }

    // N7: standard base64 and base64url ('-', '_') alphabets.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/_\-])[A-Za-z0-9+/_\-]{43,}={0,2}(?![A-Za-z0-9+/_\-=])", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Run();

    /// <summary>Masks base64 runs ≥43 chars (32-byte keys) that mix upper and lower case (bech32 addresses are lowercase).</summary>
    private static string RedactBase64(string s) => Base64Run().Replace(s, m =>
        m.Value.Any(char.IsUpper) && m.Value.Any(char.IsLower) ? Mask : m.Value);

    private static HashSet<string> LoadWords()
    {
        var wl = Wordlist.English;
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < wl.Count; i++) set.Add(wl[i]);
        return set;
    }
}
