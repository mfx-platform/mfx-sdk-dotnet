using System.Text.Json;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// Prices, volumes and amounts are exact: a decimal on the wire is a JSON string, read and written
/// without passing through binary floating point. Core proves this rule here; the shared suite's
/// decimals cases go through trading methods and are claimed in Enriched.
/// </summary>
public sealed class DecimalsTests
{
    private sealed record Quote(decimal Bid, decimal? Ask);

    /// <summary>
    /// Given prices written as JSON strings, with trailing zeros, a sign, and more digits than a
    /// double holds / When they are read and written back / Then each is the exact decimal, its scale
    /// kept, and it goes back out as the same string.
    /// </summary>
    [Theory]
    [InlineData("1.10450")]
    [InlineData("0.21")]
    [InlineData("-0.5")]
    [InlineData("1.10")]
    [InlineData("12345678901234567.123456789")]
    public void A_decimal_string_reads_exactly_and_writes_back_the_same(string text)
    {
        var quote = JsonSerializer.Deserialize<Quote>($$"""{"Bid":"{{text}}","Ask":null}""", MfxJson.Options)!;

        Assert.Equal(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), quote.Bid);
        Assert.Null(quote.Ask);
        Assert.Equal($$"""{"Bid":"{{text}}"}""", JsonSerializer.Serialize(quote, MfxJson.Options));
    }

    /// <summary>
    /// Given a price written as a JSON number, and strings that are not plain decimals (an exponent,
    /// a separator, spaces, empty) / When they are read / Then each is refused, because a number token
    /// has already been through binary floating point.
    /// </summary>
    [Theory]
    [InlineData("0.21000000000000002")]
    [InlineData("1")]
    [InlineData("\"1e5\"")]
    [InlineData("\"1,000.5\"")]
    [InlineData("\" 1.5\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    public void A_decimal_that_is_not_a_plain_string_is_refused(string token)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Quote>($$"""{"Bid":{{token}}}""", MfxJson.Options));
    }

    /// <summary>
    /// Given the arithmetic a double gets wrong / When the same values are carried as decimals /
    /// Then 0.1 + 0.2 is 0.3 exactly, and the sum goes out as "0.3".
    /// </summary>
    [Fact]
    public void Decimal_arithmetic_stays_exact_where_a_double_does_not()
    {
        var quote = JsonSerializer.Deserialize<Quote>("""{"Bid":"0.1","Ask":"0.2"}""", MfxJson.Options)!;

        Assert.NotEqual(0.3, 0.1 + 0.2);
        Assert.Equal(0.3m, quote.Bid + quote.Ask);
        Assert.Equal("\"0.3\"", JsonSerializer.Serialize(quote.Bid + quote.Ask!.Value, MfxJson.Options));
        Assert.True(MfxJson.Options.IsReadOnly);
    }
}
