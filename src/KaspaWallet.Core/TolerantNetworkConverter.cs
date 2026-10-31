using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaspaWallet.Core;

/// <summary>
/// R3-6: settings.json accepts WalletNetwork as a string ("Mainnet", "testnet10", "Testnet-10") or a number, and writes strings.
/// Unknown values become an undefined enum value that <see cref="WalletSettings.Normalize"/> resets (per field, with a notice)
/// instead of making the whole file unreadable.
/// </summary>
public sealed class TolerantNetworkConverter : JsonConverter<WalletNetwork>
{
    private static int _unknown = -1;

    public static WalletNetwork Parse(string? text)
    {
        var t = (text ?? "").Replace("-", "").Replace("_", "").Trim();
        foreach (var v in Enum.GetValues<WalletNetwork>())
            if (string.Equals(v.ToString(), t, StringComparison.OrdinalIgnoreCase)) return v;
        if (int.TryParse(t, out var n)) return (WalletNetwork)n;
        return (WalletNetwork)Interlocked.Decrement(ref _unknown);
    }

    public override WalletNetwork Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetInt32(out var n) => (WalletNetwork)n,
        JsonTokenType.String => Parse(reader.GetString()),
        _ => (WalletNetwork)Interlocked.Decrement(ref _unknown),
    };

    public override void Write(Utf8JsonWriter writer, WalletNetwork value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());

    public override WalletNetwork ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, WalletNetwork value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());
}
