using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MfxHub.Models;

namespace MfxHub.Sdk.Conformance;

/// <summary>The shared suite's copy operations (vocabulary.json, mfx-sdk-conformance 0.8.0) in .NET: nothing more.</summary>
internal static class CopyOps
{
    public static bool Handles(string op) => op.StartsWith("copy.", StringComparison.Ordinal);

    private static Guid Id(JsonObject args, string name) => Guid.Parse(args[name]!.GetValue<string>(), CultureInfo.InvariantCulture);

    private static string Text(JsonObject args, string name) => args[name]!.GetValue<string>();

    private static JsonNode Tree(object model) => JsonSerializer.SerializeToNode(model, model.GetType(), MfxJson.Options)!;

    private static JsonNode? Field(JsonNode? node, string name) => (node as JsonObject)?[name]?.DeepClone();

    private static JsonArray Each(JsonNode? rows, string name) =>
        [.. (rows as JsonArray ?? []).Select(row => Field(row, name))];

    private static List<TEnum> Words<TEnum>(JsonNode? values)
        where TEnum : struct, Enum => (values as JsonArray ?? []).Select(v => SuiteTests.Word<TEnum>(v!.GetValue<string>())).ToList();

    private static DateTimeOffset? Instant(JsonObject args, string name) =>
        args[name] is { } value ? DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;

    public static async Task<Dictionary<string, JsonNode?>> CallAsync(MfxClient client, string op, JsonObject args)
    {
        var copy = client.Copy;
        switch (op)
        {
            case "copy.create_leader":
            {
                var leader = Tree(await copy.CreateLeaderAsync(SuiteTests.Word<CopyLeaderSource>(Text(args, "source")),
                    args["signal_lifetime_s"]!.GetValue<int>(), Text(args, "idempotency_key"),
                    accountId: args["account_id"] is null ? null : Id(args, "account_id")));
                return new() { ["id"] = Field(leader, "id"), ["source"] = Field(leader, "source"), ["state"] = Field(leader, "state") };
            }
            case "copy.leaders":
            {
                var states = Words<CopyLeaderState>(args["state"]);
                var ids = new JsonArray();
                JsonNode? stats = null;
                string? cursor = null;
                do
                {
                    var page = await copy.LeadersAsync(cursor, state: states);
                    foreach (var item in page.Items ?? [])
                    {
                        ids.Add(item.Id.ToString("D", CultureInfo.InvariantCulture));
                    }
                    stats = page.Stats is null ? null : Tree(page.Stats);
                    cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
                }
                while (cursor is not null);
                return new() { ["ids"] = ids, ["stats_leaders"] = Field(stats, "leaders") };
            }
            case "copy.send_open_signal":
            {
                var accepted = Tree(await copy.SendOpenSignalAsync(Id(args, "leader_id"), Text(args, "id"), Text(args, "symbol"),
                    SuiteTests.Word<Side>(Text(args, "side")), Text(args, "idempotency_key"), volume: SuiteTests.Exact(args["volume"])));
                return new() { ["signal_id"] = Field(accepted, "signal_id"), ["duplicate"] = Field(accepted, "duplicate") };
            }
            case "copy.create_follower":
            {
                var follower = Tree(await copy.CreateFollowerAsync(Id(args, "account_id"), Id(args, "leader_id"), Text(args, "idempotency_key"),
                    scaling: args["scaling"]!.DeepClone(), options: args["options"]?.DeepClone()));
                return new() { ["id"] = Field(follower, "id"), ["state"] = Field(follower, "state"), ["mandate_version"] = Field(follower, "mandate_version") };
            }
            case "copy.preview_follower":
            {
                var rows = args["rows"]!.AsArray().Select(r => (object)r!.DeepClone()).ToList();
                var preview = Tree(await copy.PreviewFollowerAsync(Id(args, "account_id"), Id(args, "leader_id"), args["scaling"]!.DeepClone(), rows));
                return new()
                {
                    ["refusal"] = Field(preview, "refusal"),
                    ["follower_volumes"] = Each(preview["rows"], "follower_volume"),
                    ["reasons"] = Each(preview["rows"], "reason"),
                };
            }
            case "copy.unfollow":
                return new()
                {
                    ["state"] = Field(Tree(await copy.UnfollowAsync(Id(args, "follower_id"), args["close_copied"]!.GetValue<bool>(),
                        Text(args, "idempotency_key"))), "state"),
                };
            case "copy.reset_stopouts":
                return new()
                {
                    ["state"] = Field(Tree(await copy.ResetStopoutsAsync(Id(args, "follower_id"), Text(args, "reason"), Text(args, "idempotency_key"))), "state"),
                };
            case "copy.log":
            {
                // A reason outside the closed list is no CopyReason: Word refuses it, before anything is sent.
                var page = await copy.LogAsync(outcome: Words<CopyOutcome>(args["outcome"]), reason: Words<CopyReason>(args["reason"]),
                    @from: Instant(args, "from"), to: Instant(args, "to"));
                var items = page.Items?.ToList() ?? [];
                return new()
                {
                    ["count"] = items.Count,
                    ["reasons"] = new JsonArray([.. items.Select(i => Field(Tree(i), "reason"))]),
                    ["stats_refused"] = page.Stats is null ? null : Field(Tree(page.Stats), "refused"),
                };
            }
            case "copy.add_group_followers":
            {
                var ids = args["follower_ids"]!.AsArray().Select(f => Guid.Parse(f!.GetValue<string>(), CultureInfo.InvariantCulture)).ToList();
                var result = Tree(await copy.AddGroupFollowersAsync(Id(args, "group_id"), ids, Text(args, "idempotency_key")));
                return new() { ["outcomes"] = Each(result["results"], "outcome"), ["reasons"] = Each(result["results"], "reason") };
            }
            case "copy.close_group":
            {
                var result = Tree(await copy.CloseGroupAsync(Id(args, "group_id"), Text(args, "reason"), args["pause"]!.GetValue<bool>(),
                    Text(args, "idempotency_key")));
                return new() { ["batch_id"] = Field(result, "batch_id"), ["paused"] = Field(result, "paused"), ["closes"] = Field(result, "closes") };
            }
            case "copy.rollover_group":
            {
                var result = Tree(await copy.RolloverGroupAsync(Id(args, "group_id"), args["dry_run"]!.GetValue<bool>(),
                    fields: args["fields"] is null ? null : Words<CopyRolloverField>(args["fields"]),
                    after: args["after"] is null ? null : Id(args, "after"), limit: args["limit"]?.GetValue<int>(),
                    idempotencyKey: args["idempotency_key"]?.GetValue<string>()));
                return new()
                {
                    ["outcomes"] = Each(result["results"], "outcome"),
                    ["reasons"] = Each(result["results"], "reason"),
                    ["next_after"] = Field(result, "next_after"),
                };
            }
            case "copy.closes":
            {
                var states = Words<CopyCloseState>(args["state"]);
                Guid? batch = args["batch_id"] is null ? null : Id(args, "batch_id");
                var ids = new JsonArray();
                var kept = new JsonArray();
                JsonNode? stats = null;
                string? cursor = null;
                do
                {
                    var page = await copy.ClosesAsync(cursor, batchId: batch, state: states);
                    foreach (var item in page.Items ?? [])
                    {
                        var row = Tree(item);
                        ids.Add(Field(row, "id"));
                        kept.Add(Field(row, "state"));
                    }
                    stats = page.Stats is null ? null : Tree(page.Stats);
                    cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
                }
                while (cursor is not null);
                return new() { ["ids"] = ids, ["states"] = kept, ["stats_incomplete"] = Field(stats, "incomplete") };
            }
            case "copy.read_events":
                return await ReadEventsAsync(client, args["count"]!.GetValue<int>());
            default:
                throw new InvalidOperationException("no copy op " + op);
        }
    }

    /// <summary>Opens the stream, subscribes events, reads count messages as the SDK's typed copy events, and closes.</summary>
    private static async Task<Dictionary<string, JsonNode?>> ReadEventsAsync(MfxClient client, int count)
    {
        var (types, accounts, venues, reasons) = (new JsonArray(), new JsonArray(), new JsonArray(), new JsonArray());
        var stream = await client.StreamAsync(new StreamOptions { MaxReconnects = 3 });
        await using (stream)
        {
            await stream.SubscribeAsync(Channel.Events);
            await foreach (var streamEvent in stream)
            {
                if (streamEvent is not StreamMessage message)
                {
                    continue;
                }
                types.Add(message.Type);
                var typed = MfxEvent.Of(message);
                accounts.Add(typed is CopyFollowerEvent copied ? copied.AccountId.ToString("D", CultureInfo.InvariantCulture) : null);
                venues.Add(typed is CopyFollowerEvent on ? on.Venue : null);
                reasons.Add(typed is CopyRefused refused ? refused.Reason : null);
                if (types.Count >= count)
                {
                    break;
                }
            }
        }
        return new() { ["types"] = types, ["accounts"] = accounts, ["venues"] = venues, ["reasons"] = reasons };
    }
}
