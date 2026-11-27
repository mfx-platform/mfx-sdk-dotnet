using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MfxHub;

/// <summary>
/// The SDK's JSON: the API's field names as they are written, and every price, volume and amount an
/// exact <see cref="decimal"/> carried as a JSON string.
/// </summary>
public static class MfxJson
{
    /// <summary>
    /// The options every body is read and written with. Exact decimals; the API's own field names;
    /// absent fields are absent, not null.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>
    /// The same options, for a request body only: an enum's <c>UnknownValue</c> is refused here, never
    /// written, because a request carries only words the contract lists.
    /// </summary>
    internal static JsonSerializerOptions Requests { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new ExactDecimalConverter());
        options.Converters.Add(new ExactNullableDecimalConverter());
        options.Converters.Add(new ContractEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>
/// Every enum the contract writes as words, as words, wherever it appears: in a list or a map as well
/// as in a property. NSwag puts a string converter on an enum property only, so without this a list of
/// timeframes would go on the wire as numbers and a list of usages would not read at all. An enum the
/// contract writes as numbers (such as a refresh rate in milliseconds) carries no wire names and is
/// left as a number. A number where the contract says a word is refused.
/// </summary>
/// <remarks>
/// A generated enum also has <c>UnknownValue</c>, which no word names: a word this version of the SDK
/// was not generated with, from a newer API, reads as it instead of failing the call. A request body
/// refuses it, so a request never carries it. <c>tools/generate.ps1</c> points every generated enum
/// property at this converter, and adds the member.
/// </remarks>
internal sealed class ContractEnumConverter : JsonConverterFactory
{
    /// <inheritdoc/>
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsEnum
        && typeToConvert.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Any(f => f.IsDefined(typeof(JsonStringEnumMemberNameAttribute), inherit: false));

    /// <inheritdoc/>
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ContractWordConverter<>).MakeGenericType(typeToConvert))!;
}

/// <summary>The words of one enum the contract writes as words, and its <c>UnknownValue</c> if it has one.</summary>
internal static class ContractWords<TEnum>
    where TEnum : struct, Enum
{
    public static readonly IReadOnlyDictionary<string, TEnum> ByWord = Words().ToDictionary(p => p.Word, p => p.Value, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<TEnum, string> ByValue = Words().ToDictionary(p => p.Value, p => p.Word);

    /// <summary>The member a word this SDK does not know reads as; null for an enum that has none.</summary>
    public static readonly TEnum? Unknown =
        Enum.TryParse<TEnum>(UnknownEnumValue.Name, ignoreCase: false, out var unknown) && UnknownEnumValue.Is(unknown) ? unknown : null;

    private static IEnumerable<(string Word, TEnum Value)> Words() =>
        typeof(TEnum).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (Attribute: f.GetCustomAttributes(typeof(JsonStringEnumMemberNameAttribute), false)
                    .OfType<JsonStringEnumMemberNameAttribute>().FirstOrDefault(), Field: f))
            .Where(p => p.Attribute is not null)
            .Select(p => (p.Attribute!.Name, (TEnum)p.Field.GetValue(null)!));
}

/// <summary>
/// The member a generated enum reads a word as when this version of the SDK does not know the word:
/// named <c>UnknownValue</c>, with the number -1.
/// </summary>
internal static class UnknownEnumValue
{
    // DECISION: the member is UnknownValue = -1, not Unknown. Eleven contract enums already have the
    // word "unknown" (CommandState's UNKNOWN among them, an order that may have been placed), which
    // .NET names Unknown; a word this SDK does not know must never read as that. -1 is outside NSwag's
    // 0..n, so the member cannot take a generated member's number.
    public const string Name = "UnknownValue";

    /// <summary>Whether <paramref name="value"/> is a generated enum's <c>UnknownValue</c>.</summary>
    public static bool Is<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Convert.ToInt64(value, CultureInfo.InvariantCulture) == -1 && Enum.GetName(value) == Name;

    /// <summary>The refusal for <c>UnknownValue</c> where a word must be sent.</summary>
    public static string Refusal(Type type) =>
        $"{type.Name}.{Name} stands for a word this version of the SDK does not know; it is read from an answer and never sent";
}

/// <summary>One enum as its contract words, reading a word it does not know as <c>UnknownValue</c>, which a request body refuses.</summary>
internal sealed class ContractWordConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"{typeof(TEnum).Name} is a word, not a {reader.TokenType}");
        }
        return FromWord(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(ToWord(value, options));
    }

    public override TEnum ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        FromWord(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(ToWord(value, options));
    }

    private static TEnum FromWord(string word) =>
        ContractWords<TEnum>.ByWord.TryGetValue(word, out var value) ? value
        : ContractWords<TEnum>.Unknown ?? throw new JsonException($"'{word}' is not a {typeof(TEnum).Name}");

    // DECISION: UnknownValue is refused in a request body (MfxJson.Requests) and written as its own name,
    // UnknownValue, anywhere else: an answer kept or logged as JSON still serialises, and reads back as
    // UnknownValue, since no contract word is that name.
    private static string ToWord(TEnum value, JsonSerializerOptions options) =>
        ContractWords<TEnum>.ByValue.TryGetValue(value, out var word) ? word
        : !UnknownEnumValue.Is(value) ? throw new JsonException($"{value} is not a {typeof(TEnum).Name}")
        : ReferenceEquals(options, MfxJson.Requests) ? throw new JsonException(UnknownEnumValue.Refusal(typeof(TEnum)))
        : UnknownEnumValue.Name;
}

/// <summary>
/// A <see cref="decimal"/> as the API writes it: a JSON string such as <c>"1.10450"</c>, read and
/// written exactly.
/// </summary>
/// <remarks>
/// A JSON number token is refused where the contract says string, because a number has already
/// passed through binary floating point in whatever wrote it: <c>0.1 + 0.2</c> is not <c>0.3</c>,
/// and a price that is one ulp off is a different price. The scale is kept, so <c>"1.10"</c>
/// round-trips as <c>"1.10"</c>. An exponent, a thousands separator or a currency sign is refused.
/// </remarks>
public sealed class ExactDecimalConverter : JsonConverter<decimal>
{
    private const NumberStyles Shape = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    /// <inheritdoc/>
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"a decimal is a JSON string such as \"1.10450\", not a {reader.TokenType}: a number token has "
                + "already been through binary floating point");
        }
        return Parse(reader.GetString()!);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A decimal from the API's text, exactly; refused when it is not plain decimal text.</summary>
    internal static decimal Parse(string text) =>
        text.Length > 0 && !char.IsWhiteSpace(text[0]) && !char.IsWhiteSpace(text[^1])
            && decimal.TryParse(text, Shape, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a decimal as the API writes one, such as \"1.10450\"");
}

/// <summary>A nullable <see cref="decimal"/> as the API writes it: a JSON string or null.</summary>
public sealed class ExactNullableDecimalConverter : JsonConverter<decimal?>
{
    private static readonly ExactDecimalConverter Exact = new();

    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Exact.Read(ref reader, typeof(decimal), options);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is { } exact)
        {
            Exact.Write(writer, exact, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
