using System.Text.Json;
using System.Text.Json.Nodes;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>Copy Trading's leaders.</summary>
public sealed class CopyLeaderTests
{
    private static readonly Guid Leader = Guid.Parse(FakeApi.Id1);
    private static readonly string[] Symbols = ["EURUSD"];
    private static string LeaderRow => FakeApi.R("""{"id": "@Id1", "source": "account", "state": "active", "signal_lifetime_s": 30}""");

    /// <summary>
    /// Given an account and a filters setting holding a decimal / When it is made a leader / Then one POST of
    /// /copy/leaders carries the source, lifetime, account and setting in the contract's names, the decimal
    /// as text, with the key; a setting with a name the contract does not have, a word outside its list, or a
    /// double, is refused before anything is sent.
    /// </summary>
    [Fact]
    public async Task A_leader_is_made_with_settings_checked_against_the_contract()
    {
        using var api = new FakeApi().Answer(LeaderRow, 201);
        using var client = api.Client();
        var account = Guid.Parse(FakeApi.Id2);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Copy.CreateLeaderAsync(CopyLeaderSource.Account, 30, "k", account,
            filters: new { symbolz = Symbols }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Copy.CreateLeaderAsync(CopyLeaderSource.Account, 30, "k", account,
            filters: new JsonObject { ["side"] = "upwards" }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Copy.CreateLeaderAsync(CopyLeaderSource.Account, 30, "k", account,
            limits: new { max_risk_pct = 1.5 }));
        Assert.Empty(api.Requests);
        await client.Copy.CreateLeaderAsync(CopyLeaderSource.Account, 30, "lead-1", account, limits: new { max_risk_pct = 1.50m });

        var sent = api.Only;
        Assert.Equal(("POST", "/v1/copy/leaders", "lead-1"), (sent.Method, sent.PathAndQuery, sent.Key));
        Assert.Equal(FakeApi.R("""{"source":"account","signal_lifetime_s":30,"account_id":"@Id2","limits":{"max_risk_pct":"1.50"}}"""), sent.Body);
    }

    /// <summary>
    /// Given leaders on two pages / When a page is read by two states and a source, and every leader is
    /// walked / Then the states go comma-separated with the source, the page keeps its stats, and the walk
    /// follows the cursor; each leader write goes to its route with its key.
    /// </summary>
    [Fact]
    public async Task Leaders_are_listed_with_their_stats_and_each_write_has_its_route()
    {
        using var api = new FakeApi().Answer("{\"stats\": {\"active\": 1}, \"items\": [" + LeaderRow + "], \"next_cursor\": \"c-2\"}")
            .Answer(FakeApi.PageOf("c-2", LeaderRow)).Answer(FakeApi.PageOf(null, LeaderRow))
            .Answer(LeaderRow).Answer(LeaderRow).Answer(LeaderRow).Answer(LeaderRow).Answer(LeaderRow);
        using var client = api.Client();
        var c = client.Copy;

        var page = await c.LeadersAsync(state: [CopyLeaderState.Active, CopyLeaderState.Paused], source: CopyLeaderSource.External);
        var all = 0;
        await foreach (var _ in c.AllLeadersAsync(limit: 1))
        {
            all++;
        }
        await c.GetLeaderAsync(Leader);
        await c.UpdateLeaderAsync(Leader, "upd-1", signalLifetimeS: 60);
        await c.PauseLeaderAsync(Leader, "p-1");
        await c.ResumeLeaderAsync(Leader, "r-1");
        await c.RetireLeaderAsync(Leader, closeCopied: true, "done", "ret-1");

        Assert.Equal("c-2", page.NextCursor);
        Assert.Equal(1, JsonSerializer.SerializeToNode(page.Stats, MfxJson.Options)!["active"]!.GetValue<int>());
        Assert.Equal(2, all);
        var p = "/v1/copy/leaders";
        Assert.Equal(
            [("GET", p + "?state=active%2Cpaused&source=external", null), ("GET", p + "?limit=1", null), ("GET", p + "?cursor=c-2&limit=1", null),
             ("GET", p + "/" + FakeApi.Id1, null), ("PATCH", p + "/" + FakeApi.Id1, "upd-1"), ("POST", p + "/" + FakeApi.Id1 + "/pause", "p-1"),
             ("POST", p + "/" + FakeApi.Id1 + "/resume", "r-1"), ("POST", p + "/" + FakeApi.Id1 + "/retire", "ret-1")],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        Assert.Equal("""{"signal_lifetime_s":60}""", api.Requests[4].Body);
        Assert.Equal("""{"close_copied":true,"reason":"done"}""", api.Requests[7].Body);
    }
}

/// <summary>An external leader's signals and the signals list.</summary>
public sealed class CopySignalsTests
{
    private static readonly Guid Leader = Guid.Parse(FakeApi.Id1);
    private const string Accepted = """{"signal_id": "0191f0c2-aaaa-7bbb-8ccc-000000000003", "duplicate": false}""";

    /// <summary>
    /// Given an external leader / When an open is signalled in units with a stop below zero, and its close /
    /// Then each is a POST of /copy/leaders/{id}/signals with its key: the units and the stop as decimal text,
    /// the close naming the open's id; giving both or neither of volume and units, or a negative volume, is
    /// refused before anything is sent.
    /// </summary>
    [Fact]
    public async Task An_open_takes_exactly_one_of_volume_and_units_and_its_stops_may_be_negative()
    {
        using var api = new FakeApi().Answer(Accepted, 202).Answer(Accepted, 202);
        using var client = api.Client();
        var c = client.Copy;

        await Assert.ThrowsAsync<ArgumentException>(() => c.SendOpenSignalAsync(Leader, "s-1", "OIL", Side.Buy, "k"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.SendOpenSignalAsync(Leader, "s-1", "OIL", Side.Buy, "k", volume: 1m, units: 100m));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.SendOpenSignalAsync(Leader, "s-1", "OIL", Side.Buy, "k", volume: -1m));
        Assert.Empty(api.Requests);
        await c.SendOpenSignalAsync(Leader, "s-1", "OIL", Side.Sell, "open-1", units: 1000m, sl: -2.50m);
        await c.SendCloseSignalAsync(Leader, "s-2", "s-1", "close-1");

        Assert.Equal(["open-1", "close-1"], api.Requests.Select(r => r.Key));
        Assert.All(api.Requests, r => Assert.Equal($"/v1/copy/leaders/{FakeApi.Id1}/signals", r.PathAndQuery));
        Assert.Equal("""{"id":"s-1","symbol":"OIL","side":"sell","units":"1000","sl":"-2.50"}""", api.Requests[0].Body);
        Assert.Equal("""{"id":"s-2","close":"s-1"}""", api.Requests[1].Body);
    }

    /// <summary>
    /// Given a window and two kinds / When the signals are listed / Then the window, the leader and the kinds go
    /// as the contract reads them; a window that ends before it starts is refused before any request.
    /// </summary>
    [Fact]
    public async Task Signals_are_listed_in_a_window_that_must_not_end_before_it_starts()
    {
        using var api = new FakeApi().Answer("{\"stats\": {}, \"items\": [], \"next_cursor\": null}");
        using var client = api.Client();
        var from = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Copy.SignalsAsync(@from: from, to: from.AddDays(-1)));
        await client.Copy.SignalsAsync(leaderId: Leader, kind: [CopySignalEntryKind.Fill, CopySignalEntryKind.External], @from: from);

        Assert.Equal($"/v1/copy/signals?from=2026-11-10T00%3A00%3A00%2B00%3A00&leader_id={FakeApi.Id1}&kind=fill%2Cexternal", api.Only.PathAndQuery);
    }
}

/// <summary>Followers, the preview and stop-outs.</summary>
public sealed class CopyFollowerTests
{
    private static readonly Guid Follower = Guid.Parse(FakeApi.Id1);
    private static string FollowerRow => FakeApi.R("""{"id": "@Id1", "state": "copying"}""");

    /// <summary>
    /// Given an account, a leader, a scaling and a symbol map / When it follows, and a follow is previewed /
    /// Then the follow is a POST with its key and the settings in the contract's names; the preview is a POST
    /// with no key, tried again after a 429 as a read is, and a preview with no row is refused.
    /// </summary>
    [Fact]
    public async Task A_follow_is_made_and_previewed_as_a_read()
    {
        using var api = new FakeApi().Answer(FollowerRow, 201).Answer("""{"error":{"code":"rate_limited","message":"slow"}}""", 429, "1").Answer("""{"rows": []}""");
        using var client = api.Client();
        var (account, leader) = (Guid.Parse(FakeApi.Id2), Guid.Parse(FakeApi.Id3));
        var scaling = new { mode = "fixed_multiplier", multiplier = 0.5m };

        await Assert.ThrowsAsync<ArgumentException>(() => client.Copy.PreviewFollowerAsync(account, leader, scaling, []));
        await client.Copy.CreateFollowerAsync(account, leader, "follow-1", scaling: scaling, symbolMap: new Dictionary<string, string> { ["XAUUSD"] = "GOLD" },
            closeOnStopout: true);
        await client.Copy.PreviewFollowerAsync(account, leader, scaling, [new { symbol = "EURUSD", volume = 1.00m }]);

        Assert.Equal([("POST", "/v1/copy/followers", "follow-1"), ("POST", "/v1/copy/followers/preview", null), ("POST", "/v1/copy/followers/preview", null)],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        Assert.Equal(
            FakeApi.R("""{"account_id":"@Id2","leader_id":"@Id3","scaling":{"mode":"fixed_multiplier","multiplier":"0.5"},"symbol_map":{"XAUUSD":"GOLD"},"close_on_stopout":true}"""),
            api.Requests[0].Body);
        Assert.Equal("1.00", api.Requests[1].Json!["rows"]![0]!["volume"]!.GetValue<string>());
        Assert.Equal([TimeSpan.FromSeconds(1)], api.Pauses);
    }

    /// <summary>
    /// Given a follower / When followers are listed out of step, it is updated, paused, resumed, resynced,
    /// unfollowed with its copied positions closed, its stop-outs walked and reset for one limit / Then the
    /// list sends newest when no sort is named, and each write is one request on its route with its key.
    /// </summary>
    [Fact]
    public async Task Each_follower_call_is_one_request_on_its_route()
    {
        var stopout = FakeApi.R("""{"id": "@Id2", "state": "active"}""");
        using var api = new FakeApi().Answer("{\"items\": [], \"next_cursor\": null}").Answer(FollowerRow).Answer(FollowerRow).Answer(FollowerRow)
            .Answer(FollowerRow).Answer(FollowerRow).Answer(FakeApi.PageOf(null, stopout)).Answer(FollowerRow);
        using var client = api.Client();
        var c = client.Copy;

        await c.FollowersAsync(outOfStep: true, state: [CopyFollowerState.Copying]);
        await c.UpdateFollowerAsync(Follower, "u-1", options: new { when_risk_unchecked = "copy" });
        await c.PauseFollowerAsync(Follower, "p-1");
        await c.ResumeFollowerAsync(Follower, "r-1");
        await c.ResyncFollowerAsync(Follower, "s-1");
        await c.UnfollowAsync(Follower, closeCopied: true, "un-1");
        var stopouts = 0;
        await foreach (var _ in c.AllStopoutsAsync(Follower, state: [CopyStopoutState.Active]))
        {
            stopouts++;
        }
        await c.ResetStopoutsAsync(Follower, "reviewed", "reset-1", CopyLimitKind.Daily);

        var p = "/v1/copy/followers/" + FakeApi.Id1;
        Assert.Equal(
            [("GET", "/v1/copy/followers?state=copying&sort=newest&out_of_step=true", null), ("PATCH", p, "u-1"), ("POST", p + "/pause", "p-1"),
             ("POST", p + "/resume", "r-1"), ("POST", p + "/resync", "s-1"), ("POST", p + "/unfollow", "un-1"),
             ("GET", p + "/stopouts?state=active", null), ("POST", p + "/stopouts/reset", "reset-1")],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        Assert.Equal(1, stopouts);
        Assert.Equal("""{"options":{"when_risk_unchecked":"copy"}}""", api.Requests[1].Body);
        Assert.Equal("""{"close_copied":true}""", api.Requests[5].Body);
        Assert.Equal("""{"reason":"reviewed","limit":"daily"}""", api.Requests[7].Body);
    }
}

/// <summary>Groups, their members, their close and their rollover.</summary>
public sealed class CopyGroupTests
{
    private static readonly Guid Group = Guid.Parse(FakeApi.Id1);
    private static string GroupRow => FakeApi.R("""{"id": "@Id1", "name": "Desk", "state": "active"}""");

    /// <summary>
    /// Given a group / When it is created with a cap, updated clearing the cap and setting the policy, and
    /// followers are added and removed / Then the cap goes as text, a cleared field as null and a field left
    /// out not at all, and the members are 1 to 500 ids, more or none refused before sending.
    /// </summary>
    [Fact]
    public async Task A_group_is_made_updated_and_its_members_changed()
    {
        using var api = new FakeApi().Answer(GroupRow, 201).Answer(GroupRow).Answer("""{"results": []}""").Answer("""{"results": []}""");
        using var client = api.Client();
        var c = client.Copy;

        await Assert.ThrowsAsync<ArgumentException>(() => c.AddGroupFollowersAsync(Group, [], "k"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.AddGroupFollowersAsync(Group, Enumerable.Repeat(Group, 501), "k"));
        await c.CreateGroupAsync("Desk", "g-1", grossExposureCap: 250000.00m);
        await c.UpdateGroupAsync(Group, "g-2", grossExposureCap: Change<decimal>.Clear, placementPolicy: "even");
        await c.AddGroupFollowersAsync(Group, [Guid.Parse(FakeApi.Id2)], "g-3");
        await c.RemoveGroupFollowersAsync(Group, [Guid.Parse(FakeApi.Id2)], "g-4");

        Assert.Equal("""{"name":"Desk","gross_exposure_cap":"250000.00"}""", api.Requests[0].Body);
        Assert.Equal("""{"gross_exposure_cap":null,"placement_policy":"even"}""", api.Requests[1].Body);
        var p = "/v1/copy/groups/" + FakeApi.Id1;
        Assert.Equal([("/v1/copy/groups", "g-1"), (p, "g-2"), (p + "/followers", "g-3"), (p + "/followers/remove", "g-4")],
            api.Requests.Select(r => (r.PathAndQuery, r.Key)));
        Assert.Equal(FakeApi.R("""{"follower_ids":["@Id2"]}"""), api.Requests[2].Body);
    }

    /// <summary>
    /// Given a group / When its members' copied positions are closed with the group paused, a rollover is dry
    /// run with no key and applied with one / Then each is a POST on its route, and an applied rollover with
    /// no key, or a dry run with one, is refused before anything is sent.
    /// </summary>
    [Fact]
    public async Task A_groups_close_and_rollover_follow_their_key_rules()
    {
        using var api = new FakeApi().Answer("""{"batch_id": "0191f0c2-aaaa-7bbb-8ccc-000000000003", "closes": []}""", 202)
            .Answer("""{"results": [], "next_after": null}""").Answer("""{"results": [], "next_after": null}""");
        using var client = api.Client();
        var c = client.Copy;

        await Assert.ThrowsAsync<ArgumentException>(() => c.RolloverGroupAsync(Group, dryRun: false));
        await Assert.ThrowsAsync<ArgumentException>(() => c.RolloverGroupAsync(Group, dryRun: true, idempotencyKey: "k"));
        await c.CloseGroupAsync(Group, "risk off", pause: true, "close-1");
        await c.RolloverGroupAsync(Group, dryRun: true, fields: [CopyRolloverField.Scaling]);
        await c.RolloverGroupAsync(Group, dryRun: false, limit: 100, idempotencyKey: "roll-1");

        var p = "/v1/copy/groups/" + FakeApi.Id1;
        Assert.Equal([(p + "/close", "close-1"), (p + "/rollover", null), (p + "/rollover", "roll-1")], api.Requests.Select(r => (r.PathAndQuery, r.Key)));
        Assert.Equal(["""{"reason":"risk off","pause":true}""", """{"dry_run":true,"fields":["scaling"]}""", """{"dry_run":false,"limit":100}"""],
            api.Requests.Select(r => r.Body));
    }
}

/// <summary>The copy log, requested closes, the tenant's stop-outs and the copy events.</summary>
public sealed class CopyLogAndEventsTests
{
    private static StreamMessage Message(string type, string data) =>
        new("events", 1, type, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data)!);

    private static readonly string Follower = FakeApi.R("""
        "schema_version": "1", "follower_id": "@Id1", "leader_id": "@Id2", "account_id": "@Id3", "venue": "ctrader", "at": "2026-11-17T10:00:00Z"
        """);

    /// <summary>
    /// Given the copy log, the closes and the tenant's stop-outs / When each is read with its filters / Then
    /// outcomes, reasons and venues go comma-separated with the sort, the closes carry their batch and window,
    /// and the refusal reasons are the contract's closed list, broker last.
    /// </summary>
    [Fact]
    public async Task The_log_closes_and_stopouts_send_their_filters_as_the_contract_reads_them()
    {
        var empty = "{\"stats\": {}, \"items\": [], \"next_cursor\": null}";
        using var api = new FakeApi().Answer(empty).Answer(empty).Answer(empty).Answer(FakeApi.R("""{"id": "@Id2"}"""));
        using var client = api.Client();
        var from = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        await client.Copy.LogAsync(outcome: [CopyOutcome.Refused], reason: [CopyReason.RiskRule, CopyReason.Broker], venue: [Venue.Ctrader], sort: CopyLogSort.MfxMs);
        await client.Copy.ClosesAsync(batchId: Guid.Parse(FakeApi.Id1), state: [CopyCloseState.Pending], @from: from);
        await client.Copy.TenantStopoutsAsync(state: [CopyStopoutState.Active], groupId: Guid.Parse(FakeApi.Id1));
        await client.Copy.GetCloseAsync(Guid.Parse(FakeApi.Id2));

        Assert.Equal(
            ["/v1/copy/log?outcome=refused&reason=risk_rule%2Cbroker&venue=ctrader&sort=mfx_ms",
             $"/v1/copy/closes?batch_id={FakeApi.Id1}&state=pending&from=2026-11-10T00%3A00%3A00%2B00%3A00",
             $"/v1/copy/stopouts?state=active&group_id={FakeApi.Id1}", $"/v1/copy/closes/{FakeApi.Id2}"],
            api.Requests.Select(r => r.PathAndQuery));
        Assert.Equal(16, Copy.RefusalReasons.Count);
        Assert.Equal(("mapping", "broker"), (Copy.RefusalReasons[0], Copy.RefusalReasons[^1]));
    }

    /// <summary>
    /// Given the copy events as the contract writes them / When each is read / Then a copy names its deal, what
    /// it became (As) and its stage timings; a refusal its reason and, from Risk Management, its refusal; a
    /// closed request what it closed and did not; a stop-out its limit and use; drift and a state change their
    /// figures; each naming the follower account and its venue.
    /// </summary>
    [Fact]
    public void The_copy_events_read_as_their_types()
    {
        var deal = """
            "deal": {"action": "open", "side": "buy", "units": "100000", "price": "1.1", "instrument_key": "fx:EURUSD"}
            """;
        var copied = Assert.IsType<CopyCopied>(MfxEvent.Of(Message(CopyCopied.Type, "{" + Follower + ", " + deal +
            FakeApi.R(""", "signal_id": "@Id1", "command_id": "@Id2", "as": {"symbol": "EURUSD.r", "side": "buy", "volume": "0.50"}, "latency_ms": 41.5, "stages": {"detect_ms": 3, "broker_ms": null}}"""))));
        var refused = Assert.IsType<CopyRefused>(MfxEvent.Of(Message(CopyRefused.Type, "{" + Follower + ", " + deal +
            FakeApi.R(""", "signal_id": "@Id1", "outcome": "refused", "reason": "risk_rule", "risk": {"reason": "daily_loss_reached", "rule": "daily_loss", "version": 2, "used": "1.00", "limit": "2.00", "would_be": "2.10"}}"""))));
        var closed = Assert.IsType<CopyClosedEvent>(MfxEvent.Of(Message(CopyClosedEvent.Type, "{" + Follower +
            FakeApi.R(""", "close_id": "@Id1", "cause": "unfollowed", "state": "incomplete", "closed": [{"symbol": "EURUSD", "volume": "0.5"}], "not_closed": [{"symbol": "XAUUSD", "why": "netting"}]}"""))));
        var stopout = Assert.IsType<CopyStopoutEvent>(MfxEvent.Of(Message(CopyStopoutEvent.Type, "{" + Follower +
            FakeApi.R(""", "stopout_id": "@Id1", "limit": "daily", "used": "500.00"}"""))));
        var drift = Assert.IsType<CopyDrift>(MfxEvent.Of(Message(CopyDrift.Type, "{" + Follower + ", \"drift_pct\": \"12.5\"}")));
        var state = Assert.IsType<CopyStateChanged>(MfxEvent.Of(Message(CopyStateChanged.Type, "{" + Follower + ", \"from\": \"copying\", \"to\": \"paused\", \"why\": \"paused\"}")));

        Assert.Equal((Guid.Parse(FakeApi.Id3), "ctrader"), (copied.AccountId, copied.Venue));
        Assert.Equal(("open", 100000m, "fx:EURUSD"), (copied.Deal.Action, copied.Deal.Units, copied.Deal.InstrumentKey));
        Assert.Equal(("EURUSD.r", 0.50m, 41.5), (copied.As.Symbol, copied.As.Volume, copied.LatencyMs!.Value));
        Assert.Equal(["detect_ms"], copied.Stages.Keys);
        Assert.Equal(("risk_rule", "daily_loss_reached", 2, "2.10"), (refused.Reason, refused.Risk!.Reason, refused.Risk.Version!.Value, refused.Risk.WouldBe));
        Assert.Equal(("incomplete", 0.5m, "netting"), (closed.State, closed.Closed[0].Volume, closed.NotClosed[0].Why));
        Assert.Equal(("daily", 500.00m), (stopout.Limit, stopout.Used));
        Assert.Equal(12.5m, drift.DriftPct);
        Assert.Equal(("copying", "paused"), (state.FromState, state.ToState));
    }
}
