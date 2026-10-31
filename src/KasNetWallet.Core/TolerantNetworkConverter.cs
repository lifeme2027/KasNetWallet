using System.Text.Json;
using System.Text.Json.Serialization;

namespace KasNetWallet.Core;

/// <summary>
/// R3-6: settings.json accepts WalletNetwork as a string ("Mainnet", "testnet10", "Testnet-10") or a number, and writes strings.
/// Unknown values become an undefined enum value that <see cref="WalletSettings.Normalize"/> resets (per field, with a notice)
/// instead of making the whole file unreadable.
/// </summary>
public sealed class TolerantNetworkConverter : JsonConverter<WalletNetwork>
{
    private static int _unknown = -1;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Raw = new();

    /// <summary>N15: the value as written in the file (e.g. 1.5, "foo") for an unknown network; the enum number otherwise.</summary>
    public static string Describe(WalletNetwork v) => Raw.TryGetValue((int)v, out var r) ? r : v.ToString();

    private static WalletNetwork Unknown(string raw)
    {
        var v = Interlocked.Decrement(ref _unknown);
        Raw[v] = raw.Length > 64 ? raw[..64] + "…" : raw;
        return (WalletNetwork)v;
    }

    public static WalletNetwork Parse(string? text)
    {
        var t = (text ?? "").Replace("-", "").Replace("_", "").Trim();
        foreach (var v in Enum.GetValues<WalletNetwork>())
            if (string.Equals(v.ToString(), t, StringComparison.OrdinalIgnoreCase)) return v;
        if (int.TryParse(t, out var n)) return (WalletNetwork)n;
        return Unknown(System.Text.Json.JsonSerializer.Serialize(text));
    }

    public override WalletNetwork Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetInt32(out var n) => Enum.IsDefined((WalletNetwork)n) ? (WalletNetwork)n : Unknown(n.ToString()),
        JsonTokenType.Number => Unknown(System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray())),
        JsonTokenType.String => Parse(reader.GetString()),
        _ => UnknownToken(ref reader),
    };

    private static WalletNetwork UnknownToken(ref Utf8JsonReader reader)
    {
        var t = reader.TokenType;
        reader.Skip();
        return Unknown(t switch { JsonTokenType.True => "true", JsonTokenType.False => "false", JsonTokenType.Null => "null", JsonTokenType.StartObject => "{…}", JsonTokenType.StartArray => "[…]", _ => t.ToString() });
    }

    public override void Write(Utf8JsonWriter writer, WalletNetwork value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());

    public override WalletNetwork ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, WalletNetwork value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());
}
