using System.Collections;
using System.Text.Json;

namespace MCPulse;

/// <summary>
/// Did this call succeed while returning nothing useful?
/// </summary>
/// <remarks>
/// <para>
/// This is the metric that catches the failures nobody reports: a search that finds no rows, a
/// lookup that misses, a query that comes back <c>[]</c>. The protocol calls all of those success,
/// the model gets nothing it can use, and the author never hears about it.
/// </para>
/// <para>
/// Only ever asked of a call that already succeeded — an error has its own outcome and is not also
/// "empty".
/// </para>
/// </remarks>
internal static class Emptiness
{
    internal static bool IsEmptyResult(object? result)
    {
        if (result is null)
        {
            return true;
        }

        var structured = Member(result, "StructuredContent") ?? Member(result, "structuredContent");
        if (structured is not null)
        {
            var inner = UnwrapResultEnvelope(structured);
            // What comes out of the envelope is whatever the tool returned. When that is a string
            // it gets the same reading a text part does.
            return inner is string text ? IsHollowText(text) : IsHollow(inner);
        }

        var content = Member(result, "Content") ?? Member(result, "content");
        if (content is IEnumerable parts and not string)
        {
            return IsEmptyContent(parts.Cast<object?>().ToList());
        }

        // Not a tool result shape at all — judge the thing itself.
        return IsHollow(result);
    }

    /// <summary>
    /// Undoes a single-key <c>{"result": …}</c> wrapper.
    /// </summary>
    /// <remarks>
    /// SDKs that derive an output schema from a handler's return type wrap a non-object return: a
    /// tool that returns <c>"[]"</c> arrives as <c>{"result": "[]"}</c>. Judging the envelope would
    /// quietly kill this metric — every result would be an object with one key, so nothing would
    /// ever be empty, and the one thing is_empty exists to catch would never fire.
    /// </remarks>
    private static object? UnwrapResultEnvelope(object? structured)
    {
        switch (structured)
        {
            case IDictionary<string, object?> { Count: 1 } map when map.ContainsKey("result"):
                return map["result"];

            case JsonElement { ValueKind: JsonValueKind.Object } element:
            {
                var properties = element.EnumerateObject().ToList();
                if (properties.Count == 1 && properties[0].NameEquals("result"))
                {
                    return Unbox(properties[0].Value);
                }

                return structured;
            }

            default:
                return structured;
        }
    }

    /// <summary>
    /// MCP returns content as a list of parts.
    /// </summary>
    /// <remarks>
    /// No parts is empty. One text part is the common case, and it is empty when the text is blank
    /// or when the text is itself a serialised empty collection — <c>"[]"</c> is the single most
    /// common way a tool says "nothing found" while reporting success.
    /// </remarks>
    private static bool IsEmptyContent(IReadOnlyList<object?> content)
    {
        if (content.Count == 0)
        {
            return true;
        }

        if (content.Count > 1)
        {
            return false;
        }

        var part = content[0];
        var type = Member(part, "Type") ?? Member(part, "type");
        if (Unbox(type) as string != "text")
        {
            return false;
        }

        var text = Unbox(Member(part, "Text") ?? Member(part, "text"));
        return text is string value && IsHollowText(value);
    }

    private static bool IsHollowText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        try
        {
            using var parsed = JsonDocument.Parse(trimmed);
            return IsHollow(Unbox(parsed.RootElement.Clone()));
        }
        catch (JsonException)
        {
            // Prose, not JSON. A tool that answers in a sentence has said something.
            return false;
        }
    }

    /// <summary>Empty list, empty map, blank string, or nothing at all.</summary>
    private static bool IsHollow(object? value) => value switch
    {
        null => true,
        string text => text.Trim().Length == 0,
        IDictionary map => map.Count == 0,
        ICollection collection => collection.Count == 0,
        IEnumerable sequence => !sequence.Cast<object?>().Any(),

        // A number or a boolean is an answer. 0 and false are results, not absences, and counting
        // them as empty would report working tools as broken.
        _ => false,
    };

    /// <summary>Turns a <see cref="JsonElement"/> into the plain value it holds.</summary>
    private static object? Unbox(object? value)
    {
        if (value is not JsonElement element)
        {
            return value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray().Select(item => Unbox(item)).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => Unbox(property.Value)),
            _ => null,
        };
    }

    /// <summary>Reads a named member off a dictionary, a JsonElement, or an object.</summary>
    internal static object? Member(object? value, string name)
    {
        switch (value)
        {
            case null:
                return null;

            case IDictionary<string, object?> map:
                return map.TryGetValue(name, out var found) ? found : null;

            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return element.TryGetProperty(name, out var property) ? property : null;

            default:
                try
                {
                    return value.GetType().GetProperty(name)?.GetValue(value);
                }
                catch
                {
                    return null;
                }
        }
    }
}
