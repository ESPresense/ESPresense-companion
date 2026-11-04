using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ESPresense.Converters;

/// <summary>
/// Pieces that make System.Text.Json produce, and accept, what Newtonsoft.Json did on the MQTT boundary,
/// so moving those payloads off Newtonsoft (#1846) leaves them byte-identical. Pinned by WireFormatTests
/// and by a differential test against Newtonsoft itself.
/// </summary>
internal static class NewtonsoftCompatible
{
    /// <summary>
    /// Newtonsoft's default string escaping: only <c>"</c>, <c>\</c>, control characters, U+0085, U+2028 and U+2029,
    /// with lowercase hex. System.Text.Json's encoders (even UnsafeRelaxedJsonEscaping) also escape HTML-sensitive
    /// characters, non-ASCII or supplementary-plane characters such as emoji.
    /// </summary>
    public sealed class Encoder : JavaScriptEncoder
    {
        public static Encoder Instance { get; } = new();

        private Encoder() { }

        public override int MaxOutputCharactersPerInputCharacter => 6;

        public override bool WillEncode(int unicodeScalar) =>
            unicodeScalar < 0x20 || unicodeScalar is '"' or '\\' or 0x85 or 0x2028 or 0x2029;

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (var i = 0; i < textLength; i++)
                if (WillEncode(text[i])) return i;
            return -1;
        }

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        {
            ReadOnlySpan<char> escaped = unicodeScalar switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                _ when WillEncode(unicodeScalar) => "\\u" + unicodeScalar.ToString("x4", CultureInfo.InvariantCulture),
                _ => default
            };

            if (escaped.IsEmpty)
            {
                // Not ours to escape: write it through (a scalar above the BMP is a surrogate pair). The writer
                // replaces a lone surrogate with U+FFFD before asking, as UTF-8 would; do the same if one ever arrives.
                var rune = Rune.IsValid(unicodeScalar) ? new Rune(unicodeScalar) : Rune.ReplacementChar;
                return rune.TryEncodeToUtf16(new Span<char>(buffer, bufferLength), out numberOfCharactersWritten);
            }

            if (escaped.Length > bufferLength)
            {
                numberOfCharactersWritten = 0;
                return false;
            }

            escaped.CopyTo(new Span<char>(buffer, bufferLength));
            numberOfCharactersWritten = escaped.Length;
            return true;
        }
    }

    /// <summary>
    /// Newtonsoft's double formatting: round-trippable, whole numbers keep a <c>.0</c> (<c>2.0</c>, not <c>2</c>),
    /// and NaN and the infinities are written as the strings <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c>.
    /// </summary>
    public sealed class DoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? double.Parse(reader.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
                : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (!double.IsFinite(value))
            {
                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                return;
            }

            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (text.IndexOfAny(new[] { '.', 'E', 'e' }) < 0) text += ".0";
            writer.WriteRawValue(text, skipInputValidation: true);
        }
    }

    /// <summary>
    /// Newtonsoft's reading of a JSON scalar into a string member: numbers and booleans become their text
    /// (<c>12</c> → <c>"12"</c>, <c>1.5</c> → <c>"1.5"</c>, <c>true</c> → <c>"True"</c>) instead of failing the whole message.
    /// Matters for discovery messages, where other bridges publish under the same prefix.
    /// </summary>
    public sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.Number when reader.TryGetInt64(out var l) => l.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            _ => throw new JsonException($"Unexpected {reader.TokenType} where a string was expected")
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
