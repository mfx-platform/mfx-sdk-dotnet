using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MfxHub;

/// <summary>
/// A request's query, built in the order the parameters are added. What the caller did not name is
/// left out of the request altogether, never sent empty.
/// </summary>
internal sealed class Query : List<KeyValuePair<string, string>>
{
    public Query Add(string name, string? value)
    {
        if (value is not null)
        {
            Add(new KeyValuePair<string, string>(name, value));
        }
        return this;
    }

    public Query Add(string name, int? value) =>
        Add(name, value?.ToString(CultureInfo.InvariantCulture));

    public Query Add(string name, long? value) =>
        Add(name, value?.ToString(CultureInfo.InvariantCulture));

    public Query Add(string name, Guid? value) => Add(name, value is { } id ? Wire.Id(id) : null);

    /// <summary>A flag as the contract writes one: <c>true</c> or <c>false</c>.</summary>
    public Query Add(string name, bool? value) => Add(name, value is { } flag ? (flag ? "true" : "false") : null);

    public Query Add(string name, DateTimeOffset? value) => Add(name, value is { } at ? Wire.Instant(at) : null);

    public Query Add(string name, decimal? value) => Add(name, value is { } exact ? Wire.Decimal(exact) : null);

    /// <summary>Every parameter of <paramref name="other"/>, after these.</summary>
    public Query AddRange(Query other)
    {
        base.AddRange(other);
        return this;
    }

    /// <summary>One parameter per value, repeated (<c>a=1&amp;a=2</c>).</summary>
    public Query Repeated(string name, IEnumerable<string>? values)
    {
        foreach (var value in values ?? [])
        {
            Add(name, value);
        }
        return this;
    }

    /// <summary>Several values as one comma-separated parameter; none is left out.</summary>
    public Query Joined(string name, IEnumerable<string>? values)
    {
        var list = values?.ToList();
        return list is { Count: > 0 } ? Add(name, string.Join(',', list)) : this;
    }
}

/// <summary>How values go on the wire.</summary>
internal static class Wire
{
    /// <summary>An id as the API writes one: lower-case, hyphenated.</summary>
    public static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>A path segment the caller named, such as a broker's symbol: escaped, so it stays one segment.</summary>
    public static string Segment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return Uri.EscapeDataString(value);
    }

    /// <summary>
    /// An exact decimal as plain digits, never an exponent and never a JSON number: <c>1E+3</c> is
    /// <c>"1000"</c>, and the scale the caller gave is kept (<c>0.010</c> stays <c>"0.010"</c>).
    /// </summary>
    public static string Decimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// An instant in ISO 8601 with its offset: <c>2026-11-17T10:00:00+00:00</c>, and six digits of
    /// fraction only when there is a fraction (<c>.250000</c>).
    /// </summary>
    public static string Instant(DateTimeOffset at) =>
        at.ToString(at.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy'-'MM'-'dd'T'HH':'mm':'sszzz" : "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'ffffffzzz",
            CultureInfo.InvariantCulture);

    /// <summary>The contract's word for an enum value (its <see cref="JsonStringEnumMemberNameAttribute"/>).</summary>
    public static string Word<TEnum>(TEnum value, [CallerArgumentExpression(nameof(value))] string parameter = "")
        where TEnum : struct, Enum => WireNames.Of(value, parameter);

    /// <summary>The enum value whose contract word is <paramref name="word"/>; null for a word this version does not know.</summary>
    public static TEnum? Parse<TEnum>(string? word)
        where TEnum : struct, Enum
    {
        if (word is null)
        {
            return null;
        }
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (!UnknownEnumValue.Is(value) && WireNames.Of(value, nameof(word)) == word)
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>The contract's words for several enum values; null when there are none.</summary>
    public static IEnumerable<string>? Words<TEnum>(
        IEnumerable<TEnum>? values, [CallerArgumentExpression(nameof(values))] string parameter = "")
        where TEnum : struct, Enum => values?.Select(v => WireNames.Of(v, parameter)).ToList();

    /// <summary>A body as the API reads it: its own field names, decimals as strings, absent fields absent.</summary>
    public static ReadOnlyMemory<byte> Body<T>(T body) => JsonSerializer.SerializeToUtf8Bytes(body, MfxJson.Requests);

    /// <summary>
    /// A setting the caller handed over as a mapping in the API's own names, checked: a binary
    /// floating-point number anywhere in it is refused before anything is sent, because it has
    /// already lost the digits an exact decimal keeps.
    /// </summary>
    public static JsonNode? Setting(object? value, [CallerArgumentExpression(nameof(value))] string parameter = "")
    {
        if (value is null)
        {
            return null;
        }
        var node = value as JsonNode ?? JsonSerializer.SerializeToNode(value, value.GetType(), MfxJson.Requests);
        Exact(node, parameter);
        return node?.DeepClone();
    }

    /// <summary>
    /// A <see cref="double"/> or <see cref="float"/>, or a JSON number written with a fraction or an
    /// exponent: a binary floating-point number. A whole number is not one.
    /// </summary>
    private static bool IsBinaryFloat(JsonValue leaf) => leaf.GetValue<object>() switch
    {
        double or float or Half => true,
        JsonElement { ValueKind: JsonValueKind.Number } element => element.GetRawText().AsSpan().IndexOfAny(".eE") >= 0,
        _ => false,
    };

    private static void Exact(JsonNode? node, string parameter)
    {
        switch (node)
        {
            case JsonObject map:
                foreach (var (_, child) in map)
                {
                    Exact(child, parameter);
                }
                break;
            case JsonArray list:
                foreach (var child in list)
                {
                    Exact(child, parameter);
                }
                break;
            case JsonValue leaf when IsBinaryFloat(leaf):
                throw new ArgumentException(
                    $"{parameter} holds a binary floating-point number; give an exact decimal as text", parameter);
            default:
                break;
        }
    }
}

/// <summary>Idempotency keys, as every write takes them.</summary>
internal static class Keys
{
    /// <summary>
    /// The caller's key, as given. Required: a key the SDK made up would make a retry by the caller a
    /// second order, so none is ever invented, and a blank one is refused before anything is sent.
    /// </summary>
    public static string Required(string idempotencyKey, [CallerArgumentExpression(nameof(idempotencyKey))] string parameter = "")
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("an idempotency key is the caller's own, and not blank", parameter);
        }
        return idempotencyKey;
    }
}

/// <summary>A list page as the API writes every one: its items, the next cursor and how many it holds.</summary>
/// <typeparam name="T">What the list holds.</typeparam>
internal sealed class WirePage<T>
{
    [JsonPropertyName("items")]
    public List<T>? Items { get; set; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("returned")]
    public int? Returned { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    public Page<T> ToPage() => new(Items ?? [], NextCursor, Returned, Source);
}

/// <summary>Following a cursor to the end. There is no total to check against, by design.</summary>
internal static class Pages
{
    public static async IAsyncEnumerable<T> All<T>(
        Func<string?, CancellationToken, Task<Page<T>>> page,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var answer = await page(cursor, cancellationToken).ConfigureAwait(false);
            foreach (var row in answer)
            {
                yield return row;
            }
            cursor = answer.NextCursor;
        }
        while (cursor is not null);
    }
}

/// <summary>
/// A body checked against the contract's model before it is sent, through the generated model: a
/// field name the model does not have, or a word outside an enum's list, is refused here,
/// because a misspelt setting that silently vanished would be a setting nobody chose.
/// </summary>
internal static class Shape
{
    public static void Check(Type model, JsonNode? node, string name)
    {
        var type = Nullable.GetUnderlyingType(model) ?? model;
        if (node is null)
        {
            return;
        }
        if (type.IsEnum)
        {
            if (type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Select(f => f.GetCustomAttributes(typeof(JsonStringEnumMemberNameAttribute), false).OfType<JsonStringEnumMemberNameAttribute>().FirstOrDefault()?.Name)
                    .Where(n => n is not null).ToList() is { Count: > 0 } words
                && (node is not JsonValue word || !word.TryGetValue<string>(out var text) || !words.Contains(text)))
            {
                throw new ArgumentException($"{name} is not one of {string.Join(", ", words)}: {node.ToJsonString()}", name);
            }
            return;
        }
        if (type != typeof(string) && type.GetInterfaces().Concat([type]).FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)) is { } map)
        {
            if (node is JsonObject entries)
            {
                foreach (var (key, value) in entries)
                {
                    Check(map.GetGenericArguments()[1], value, $"{name}.{key}");
                }
            }
            return;
        }
        if (type != typeof(string) && type.GetInterfaces().Concat([type]).FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } list)
        {
            if (node is JsonArray items)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    Check(list.GetGenericArguments()[0], items[i], $"{name}[{i}]");
                }
            }
            return;
        }
        if (type.Namespace != "MfxHub.Models" || !type.IsClass || node is not JsonObject fields)
        {
            return;
        }
        var properties = type.GetProperties()
            .Select(p => (Property: p, Wire: p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).OfType<JsonPropertyNameAttribute>().FirstOrDefault()?.Name))
            .Where(p => p.Wire is not null)
            .ToDictionary(p => p.Wire!, p => p.Property.PropertyType, StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (!properties.TryGetValue(key, out var field))
            {
                throw new ArgumentException($"{name}.{key} is not a field the contract has", name);
            }
            Check(field, value, $"{name}.{key}");
        }
    }

    /// <summary>
    /// <paramref name="body"/> checked against <typeparamref name="TModel"/>: its names and words by
    /// <see cref="Check"/>, and its values by reading it as the model; a value of the wrong type is refused.
    /// </summary>
    public static JsonObject Of<TModel>(JsonObject body, string name)
    {
        Check(typeof(TModel), body, name);
        try
        {
            _ = body.Deserialize<TModel>(MfxJson.Options);
        }
        catch (JsonException wrong)
        {
            throw new ArgumentException($"{name} does not fit the contract: {wrong.Message}", name, wrong);
        }
        return body;
    }
}
