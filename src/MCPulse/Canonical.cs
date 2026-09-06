using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPulse;

/// <summary>
/// JSON Canonicalization Scheme (RFC 8785).
/// </summary>
/// <remarks>
/// <para>
/// <c>ArgsHash</c> only means anything if every MCPulse SDK, in every language, turns the same
/// arguments into the same bytes. <c>System.Text.Json</c> does not get there on its own: it escapes
/// <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> by default, and .NET's number formatting writes
/// <c>1E+21</c> and <c>1E-07</c> where ECMAScript writes <c>1e+21</c> and <c>1e-7</c>. Each of those
/// silently sends the same call to a different bucket than the TypeScript SDK would.
/// </para>
/// <para>
/// So none of the serialisation below goes through a JSON writer. Every rule is spelled out, and
/// <c>canonical.json</c> — the same file the TypeScript, Python, Go and Java SDKs run — is what
/// holds this class to them.
/// </para>
/// <para>
/// One thing .NET gets right for free: RFC 8785 §3.2.3 sorts keys by UTF-16 code unit, and a .NET
/// string is UTF-16, so <see cref="StringComparer.Ordinal"/> is already the required ordering.
/// Python and Go both need a workaround here.
/// </para>
/// </remarks>
public static class Canonical
{
    /// <summary>Thrown for anything JSON cannot represent: NaN, an infinity, a cycle.</summary>
    public sealed class NotJsonException(string message) : Exception(message);

    private const int MaxDepth = 1000;

    /// <summary>The canonical JSON form of <paramref name="value"/>.</summary>
    public static string Canonicalize(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder output, object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new NotJsonException("nested too deeply");
        }

        switch (value)
        {
            case null:
                output.Append("null");
                return;

            case bool flag:
                output.Append(flag ? "true" : "false");
                return;

            case string text:
                WriteString(output, text);
                return;

            case char character:
                WriteString(output, character.ToString());
                return;

            // Every numeric type is written as the IEEE-754 double RFC 8785 limits JSON to.
            // Matching JavaScript is the point: an integer past 2^53 must lose precision here
            // exactly as it does there, or the two SDKs disagree.
            case double or float or decimal or int or long or short or byte
                or uint or ulong or ushort or sbyte:
                WriteNumber(output, System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;

            case JsonElement element:
                WriteElement(output, element, depth);
                return;

            case JsonNode node:
                WriteElement(output, node.Deserialize<JsonElement>(), depth);
                return;

            case IDictionary<string, object?> dictionary:
                WriteObject(output, dictionary, depth);
                return;

            case IDictionary raw:
                WriteObject(output, ToStringKeyed(raw), depth);
                return;

            case IEnumerable sequence:
                output.Append('[');
                var first = true;
                foreach (var item in sequence)
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    first = false;
                    Write(output, item, depth + 1);
                }

                output.Append(']');
                return;

            default:
                // A POCO or an anonymous type. Round-trip it through System.Text.Json so it
                // arrives here as one of the shapes above. The escaping the writer applies on
                // the way out is undone by the read on the way back in, so it cannot leak into
                // the canonical form.
                WriteElement(output, JsonSerializer.SerializeToElement(value), depth);
                return;
        }
    }

    private static Dictionary<string, object?> ToStringKeyed(IDictionary raw)
    {
        var mapped = new Dictionary<string, object?>(raw.Count);
        foreach (DictionaryEntry entry in raw)
        {
            if (entry.Key is not string key)
            {
                throw new NotJsonException("object key must be a string");
            }

            mapped[key] = entry.Value;
        }

        return mapped;
    }

    private static void WriteObject(
        StringBuilder output, IDictionary<string, object?> map, int depth)
    {
        // StringComparer.Ordinal compares UTF-16 code units, which is exactly what RFC 8785 asks
        // for — including above the BMP, where U+1F680 (the surrogate pair D83D DE80) sorts
        // before U+FFFD.
        var keys = map.Keys.ToList();
        keys.Sort(StringComparer.Ordinal);

        output.Append('{');
        var first = true;
        foreach (var key in keys)
        {
            if (!first)
            {
                output.Append(',');
            }

            first = false;
            WriteString(output, key);
            output.Append(':');
            Write(output, map[key], depth + 1);
        }

        output.Append('}');
    }

    private static void WriteElement(StringBuilder output, JsonElement element, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new NotJsonException("nested too deeply");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                output.Append("null");
                return;

            case JsonValueKind.True:
                output.Append("true");
                return;

            case JsonValueKind.False:
                output.Append("false");
                return;

            case JsonValueKind.String:
                WriteString(output, element.GetString() ?? string.Empty);
                return;

            case JsonValueKind.Number:
                WriteNumber(output, element.GetDouble());
                return;

            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        output.Append(',');
                    }

                    firstItem = false;
                    WriteElement(output, item, depth + 1);
                }

                output.Append(']');
                return;

            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                properties.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));

                output.Append('{');
                var firstProperty = true;
                foreach (var property in properties)
                {
                    if (!firstProperty)
                    {
                        output.Append(',');
                    }

                    firstProperty = false;
                    WriteString(output, property.Name);
                    output.Append(':');
                    WriteElement(output, property.Value, depth + 1);
                }

                output.Append('}');
                return;

            default:
                throw new NotJsonException($"cannot canonicalize {element.ValueKind}");
        }
    }

    // ─── Strings ─────────────────────────────────────────────────────────────

    private const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// A JSON string per JCS §3.2.2.2, which is ECMAScript's escaping: the short escapes where one
    /// exists, lowercase <c>\u00xx</c> for the rest of the C0 range, and nothing else touched.
    /// </summary>
    /// <remarks>
    /// In particular <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> are written literally.
    /// <c>System.Text.Json</c> escapes all three by default, and that alone would put every .NET
    /// server's hashes in a different bucket from every other SDK's.
    /// </remarks>
    private static void WriteString(StringBuilder output, string text)
    {
        output.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u00")
                            .Append(HexDigits[(c >> 4) & 0xF])
                            .Append(HexDigits[c & 0xF]);
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }

    // ─── Numbers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// ECMAScript <c>Number::toString</c>, which JCS §3.2.2.3 defers to.
    /// </summary>
    /// <remarks>
    /// .NET's round-trip format is shortest, which is half the job, but its shape is not
    /// ECMAScript's: <c>1E+21</c> rather than <c>1e+21</c>, <c>1E-07</c> rather than <c>1e-7</c>,
    /// and it enters exponent form at different thresholds.
    /// </remarks>
    private static void WriteNumber(StringBuilder output, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            // Not JSON. Coercing to null the way some encoders do would hand two genuinely
            // different calls the same hash.
            throw new NotJsonException("non-finite number");
        }

        if (value == 0)
        {
            // Covers negative zero, which JCS writes as "0".
            output.Append('0');
            return;
        }

        if (value < 0)
        {
            output.Append('-');
            value = -value;
        }

        var (digits, n) = Shortest(value);
        var k = digits.Length;

        // The five cases of ECMAScript Number::toString, in its own order.
        if (k <= n && n <= 21)
        {
            output.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            output.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            output.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            var exponent = n - 1;
            if (k == 1)
            {
                output.Append(digits);
            }
            else
            {
                output.Append(digits[0]).Append('.').Append(digits, 1, k - 1);
            }

            output.Append('e')
                .Append(exponent >= 0 ? '+' : '-')
                .Append(Math.Abs(exponent).ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Decomposes a positive finite double into its shortest round-tripping digits and the
    /// position of the decimal point: the value is <c>digits * 10^(n - digits.Length)</c>.
    /// </summary>
    private static (string Digits, int N) Shortest(double value)
    {
        // "R" is shortest round-trippable on .NET Core 3.0 and later, but its shape varies
        // between plain and exponent form, so both are handled.
        var formatted = value.ToString("R", CultureInfo.InvariantCulture);

        var exponent = 0;
        var separator = formatted.IndexOfAny(['E', 'e']);
        if (separator >= 0)
        {
            exponent = int.Parse(formatted[(separator + 1)..], CultureInfo.InvariantCulture);
            formatted = formatted[..separator];
        }

        var point = formatted.IndexOf('.');
        var integerLength = point < 0 ? formatted.Length : point;
        var digits = point < 0 ? formatted : formatted.Remove(point, 1);

        var n = exponent + integerLength;

        var trimmedStart = digits.TrimStart('0');
        n -= digits.Length - trimmedStart.Length;
        digits = trimmedStart.TrimEnd('0');

        return digits.Length == 0 ? ("0", 1) : (digits, n);
    }
}
