using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// A word a newer API adds to a list it answers with, such as a close's cause, reads as the enum's
/// <c>UnknownValue</c> instead of failing the call; a request never carries <c>UnknownValue</c>.
/// </summary>
public sealed class UnknownEnumWordTests
{
    private const string Later = "a_word_from_a_later_version";

    private static readonly Type[] WordEnums =
    [
        .. typeof(TenantTerminal).Assembly.GetExportedTypes().Where(t => t.Namespace == "MfxHub.Models" && t.IsEnum
            && t.GetFields(BindingFlags.Public | BindingFlags.Static).Any(f => f.IsDefined(typeof(JsonStringEnumMemberNameAttribute)))),
    ];

    private static string LeaderRow(string source = "account", string state = "active", string id = "@Id1") =>
        FakeApi.R($$"""{"id": "{{id}}", "source": "{{source}}", "state": "{{state}}", "signal_lifetime_s": 30}""");

    /// <summary>
    /// Given every generated enum the contract writes as words / When a word none of them lists is read,
    /// alone, in a list and as a map key, with the SDK's options and with none / Then each has UnknownValue,
    /// numbered -1, which no word names, and the unknown word reads as it; every word the enum lists still
    /// reads as its own member.
    /// </summary>
    [Fact]
    public void Every_generated_enum_reads_a_word_it_does_not_know_as_UnknownValue()
    {
        Assert.True(WordEnums.Length > 100, $"{WordEnums.Length} enums");
        foreach (var type in WordEnums)
        {
            var unknown = type.GetField("UnknownValue");
            Assert.True(unknown is not null, $"{type.Name} has no UnknownValue");
            Assert.Equal(-1, Convert.ToInt32(unknown.GetValue(null), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(unknown.GetCustomAttribute<JsonStringEnumMemberNameAttribute>());

            var read = JsonSerializer.Deserialize($"\"{Later}\"", type, MfxJson.Options);
            Assert.Equal(unknown.GetValue(null), read);
            foreach (var member in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (member.GetCustomAttribute<JsonStringEnumMemberNameAttribute>() is { } word)
                {
                    Assert.Equal(member.GetValue(null), JsonSerializer.Deserialize($"\"{word.Name}\"", type, MfxJson.Options));
                }
            }
        }

        var listed = JsonSerializer.Deserialize<List<CopyCloseCause>>($"[\"group_close\", \"{Later}\"]", MfxJson.Options);
        Assert.Equal([CopyCloseCause.GroupClose, CopyCloseCause.UnknownValue], listed);
        var keyed = JsonSerializer.Deserialize<Dictionary<CopyCloseCause, int>>($"{{\"{Later}\": 1}}", MfxJson.Options);
        Assert.Equal(1, keyed![CopyCloseCause.UnknownValue]);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CopyCloseCause>("2", MfxJson.Options));
    }

    /// <summary>
    /// Given a requested close whose cause is a word the SDK does not know / When it is read with the SDK's
    /// options and with System.Text.Json's defaults, and written back / Then the cause is UnknownValue, the
    /// known state is its member; written outside a request it is the name UnknownValue, which reads back as
    /// UnknownValue.
    /// </summary>
    [Fact]
    public void A_model_with_an_unknown_word_reads_and_is_written_outside_a_request_as_UnknownValue()
    {
        var row = FakeApi.R($$"""{"id": "@Id1", "cause": "{{Later}}", "state": "pending"}""");
        foreach (var options in new[] { MfxJson.Options, JsonSerializerOptions.Default })
        {
            var close = JsonSerializer.Deserialize<CopyClose>(row, options)!;
            Assert.Equal((CopyCloseCause.UnknownValue, CopyCloseState.Pending), (close.Cause, close.State));
            var written = JsonSerializer.SerializeToNode(close, options)!;
            Assert.Equal("UnknownValue", written["cause"]!.GetValue<string>());
            Assert.Equal(CopyCloseCause.UnknownValue, written.Deserialize<CopyClose>(options)!.Cause);
        }
    }

    /// <summary>
    /// Given the platform answers a page of leaders, one with a source and a state the SDK does not know
    /// / When the caller lists them, and then sends UnknownValue as a filter, in a typed setting and in a
    /// request body / Then the page is returned whole with UnknownValue for the unknown words; each
    /// request is refused before anything is sent, saying UnknownValue is never sent, and no text
    /// "UnknownValue" or "Unknown" reaches the wire.
    /// </summary>
    [Fact]
    public async Task A_page_with_unknown_words_reads_and_UnknownValue_is_never_sent()
    {
        using var api = new FakeApi().Answer(FakeApi.PageOf(null, LeaderRow(), LeaderRow(Later, Later, "@Id2")));
        using var client = api.Client();

        var page = await client.Copy.LeadersAsync();
        Assert.Equal(
            [(CopyLeaderSource.Account, CopyLeaderState.Active), (CopyLeaderSource.UnknownValue, CopyLeaderState.UnknownValue)],
            page.Items!.Select(l => (l.Source, l.State)));
        Assert.Single(api.Requests);

        var filter = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Copy.LeadersAsync(source: CopyLeaderSource.UnknownValue));
        Assert.Contains("never sent", filter.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Copy.LeadersAsync(state: [CopyLeaderState.UnknownValue]));
        var setting = await Assert.ThrowsAsync<JsonException>(() => client.Copy.CreateLeaderAsync(CopyLeaderSource.External, 30, "k",
            filters: new CopyLeaderFilters { Side = CopyLeaderFiltersSide.UnknownValue }));
        Assert.Contains("CopyLeaderFiltersSide.UnknownValue", setting.Message, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => Wire.Body(new CopyLeaderFilters { Side = CopyLeaderFiltersSide.UnknownValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Wire.Word(CopyCloseCause.UnknownValue));
        Assert.Null(Wire.Parse<CopyCloseCause>("UnknownValue"));
        Assert.DoesNotContain("UnknownValue", Copy.RefusalReasons);
        Assert.Single(api.Requests);
    }
}
