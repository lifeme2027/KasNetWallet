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
        return RedactMnemonicRuns(s);
    }

    private static string RedactMnemonicRuns(string s)
    {
        var matches = Word().Matches(s);
        var result = new System.Text.StringBuilder(s);
        int runStart = -1, runCount = 0;
        var spans = new List<(int start, int end)>();
        for (var i = 0; i <= matches.Count; i++)
        {
            var isWord = i < matches.Count && Words.Contains(matches[i].Value.ToLowerInvariant());
            if (isWord && runCount > 0)
            {
                // must be separated only by whitespace from previous word
                var prev = matches[i - 1];
                var gap = s.AsSpan(prev.Index + prev.Length, matches[i].Index - prev.Index - prev.Length);
                if (!gap.IsWhiteSpace()) { Flush(i); runStart = i; runCount = 1; continue; }
            }
            if (isWord) { if (runCount == 0) runStart = i; runCount++; }
            else Flush(i);
        }
        for (var k = spans.Count - 1; k >= 0; k--)
            result.Remove(spans[k].start, spans[k].end - spans[k].start).Insert(spans[k].start, Mask);
        return result.ToString();

        void Flush(int i)
        {
            if (runCount >= 12)
            {
                var first = matches[runStart]; var last = matches[runStart + runCount - 1];
                spans.Add((first.Index, last.Index + last.Length));
            }
            runCount = 0; runStart = -1;
        }
    }

    private static HashSet<string> LoadWords()
    {
        var wl = Wordlist.English;
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < wl.Count; i++) set.Add(wl[i]);
        return set;
    }
}
