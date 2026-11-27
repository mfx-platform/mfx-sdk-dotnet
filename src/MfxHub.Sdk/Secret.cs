using System.Text.Json;
using System.Text.Json.Serialization;

namespace MfxHub;

/// <summary>
/// A value that cannot reach a log by being printed, such as a broker password. It renders as
/// <c>***</c> everywhere except <see cref="Reveal"/>, and it is never serialised.
/// </summary>
/// <remarks>
/// A password travels through the caller's code, this SDK and an HTTP request, past string
/// interpolation, loggers and exception messages. Revealing it takes the one explicit call that is
/// easy to see in review.
/// </remarks>
[JsonConverter(typeof(SecretRefusingConverter))]
public sealed class Secret : IEquatable<Secret>
{
    private readonly string _value;

    /// <summary>A secret holding <paramref name="value"/>.</summary>
    /// <param name="value">The value itself.</param>
    public Secret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>A string as a secret, so a call that takes one can be handed a string.</summary>
    /// <param name="value">The value itself.</param>
    public static implicit operator Secret(string value) => new(value);

    /// <summary>A string as a secret.</summary>
    /// <param name="value">The value itself.</param>
    /// <returns>The secret.</returns>
    public static Secret FromString(string value) => new(value);

    /// <summary>The value itself. Every call is a place a secret can escape: keep them few.</summary>
    /// <returns>The value.</returns>
    public string Reveal() => _value;

    /// <inheritdoc/>
    public override string ToString() => "Secret(***)";

    /// <inheritdoc/>
    public bool Equals(Secret? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Secret other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine("mfx.Secret", _value);
}

/// <summary>Refuses to write or read a <see cref="Secret"/>: a serialised secret is a secret written somewhere by accident.</summary>
internal sealed class SecretRefusingConverter : JsonConverter<Secret>
{
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("a Secret is never deserialised; read it from your own secret store");

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
        throw new JsonException("a Secret is never serialised; read it from your own secret store");
}
