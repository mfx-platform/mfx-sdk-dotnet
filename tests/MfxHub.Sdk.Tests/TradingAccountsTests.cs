using System.Text.Json;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>A trading account's reads.</summary>
public sealed class TradingAccountReadsTests
{
    private static readonly Guid Account = Guid.Parse(FakeApi.Id1);

    private static string Row(string id, string venue = "ctrader") =>
        $$"""{"id": "{{id}}", "venue": "{{venue}}", "state": "CONNECTED", "broker": {}, "runners": [], "created_at": "2026-11-17T09:00:00Z"}""";

    /// <summary>
    /// Given accounts on two pages / When they are listed by venue, environment and state / Then each
    /// filter goes in the contract's words, several values as one comma-separated value, and the cursor
    /// is followed; a value the enum does not define is refused before any request.
    /// </summary>
    [Fact]
    public async Task Accounts_are_listed_by_venue_environment_and_state()
    {
        using var api = new FakeApi().Answer(FakeApi.PageOf("c-2", Row(FakeApi.Id1))).Answer(FakeApi.PageOf(null, Row(FakeApi.Id2, "mt5"))).Answer(Row(FakeApi.Id1));
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TradingAccounts.PageAsync(venue: [(TradingAccountVenue)9]));
        var venues = new List<TradingAccountVenue>();
        await foreach (var a in client.TradingAccounts.ListAsync(limit: 1, venue: [TradingAccountVenue.Ctrader, TradingAccountVenue.Mt5],
            environment: TradingAccountEnvironment.Demo, state: [TradingAccountState.Connected, TradingAccountState.ReauthNeeded]))
        {
            venues.Add(a.Venue);
        }
        var one = await client.TradingAccounts.GetAsync(Account);

        Assert.Equal([TradingAccountVenue.Ctrader, TradingAccountVenue.Mt5], venues);
        Assert.Equal(TradingAccountState.Connected, one.State);
        Assert.Equal(
            [
                "/v1/accounts?limit=1&venue=ctrader%2Cmt5&environment=demo&state=CONNECTED%2CREAUTH_NEEDED",
                "/v1/accounts?cursor=c-2&limit=1&venue=ctrader%2Cmt5&environment=demo&state=CONNECTED%2CREAUTH_NEEDED",
                "/v1/accounts/" + FakeApi.Id1,
            ],
            api.Requests.Select(r => r.PathAndQuery));
    }

    /// <summary>
    /// Given an account / When its funds, positions, pending orders, symbols and one position's deals are
    /// read / Then each is one GET under /accounts/{id}, and the deals carry the window and the position.
    /// </summary>
    [Fact]
    public async Task Each_account_read_is_one_request_under_its_account()
    {
        using var api = new FakeApi().Answer($$"""{"terminal_id": "{{FakeApi.Id1}}", "stale": true}""")
            .Answer("""{"as_of": "2026-11-17T09:00:00Z", "stale": false, "items": []}""")
            .Answer("""{"as_of": "2026-11-17T09:00:00Z", "stale": false, "items": []}""")
            .Answer($$"""{"account_id": "{{FakeApi.Id1}}", "as_of": "2026-11-17T09:00:00Z", "items": []}""")
            .Answer(FakeApi.PageOf(null));
        using var client = api.Client();
        var t = client.TradingAccounts;
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var funds = await t.FundsAsync(Account);
        await t.PositionsAsync(Account);
        await t.PendingOrdersAsync(Account);
        await t.SymbolsAsync(Account);
        await t.DealsAsync(Account, from, from.AddDays(31), position: 778899);

        Assert.True(funds.Stale);
        var p = "/v1/accounts/" + FakeApi.Id1;
        Assert.Equal(
            [p + "/account", p + "/positions", p + "/orders/pending", p + "/symbols",
             p + "/history/deals?from=2026-11-01T00%3A00%3A00%2B00%3A00&to=2026-12-02T00%3A00%3A00%2B00%3A00&position=778899"],
            api.Requests.Select(r => r.PathAndQuery));
    }
}

/// <summary>
/// A trading account's commands: the same bodies and rules as execution, sent under /accounts/{id}, each
/// a result.
/// </summary>
public sealed class TradingAccountCommandsTests
{
    private static readonly Guid Account = Guid.Parse(FakeApi.Id1);
    private const string Accepted = """{"command_id": "0191f0c2-aaaa-7bbb-8ccc-000000000002", "state": "ACCEPTED"}""";

    /// <summary>
    /// Given a cTrader account / When an order is placed and each position command is sent / Then each is
    /// one request under /accounts/{id} with the caller's key and the body execution sends, decimals as
    /// plain text.
    /// </summary>
    [Fact]
    public async Task Commands_go_under_the_account_with_executions_bodies()
    {
        using var api = new FakeApi();
        for (var i = 0; i < 9; i++)
        {
            api.Answer(Accepted, 202);
        }
        using var client = api.Client();
        var t = client.TradingAccounts;

        var placed = await t.PlaceOrderAsync(Account, "EURUSD", Side.Buy, 0.10m, "k-0", magic: 7);
        await t.CancelOrderAsync(Account, 1, "k-1");
        await t.ModifyOrderAsync(Account, 1, "k-2", price: 1.1m);
        await t.ModifyPositionAsync(Account, 2, "k-3", sl: 1.05m);
        await t.ClosePositionAsync(Account, 2, "k-4");
        await t.ClosePositionByAsync(Account, 2, 3, "k-5");
        await t.ClosePositionPartialAsync(Account, 2, 0.05m, "k-6");
        await t.SetTrailingStopAsync(Account, 2, 0.001m, "k-7");
        await t.ClearTrailingStopAsync(Account, 2, "k-8");

        Assert.Equal(CommandState.Accepted, placed.State);
        var p = "/v1/accounts/" + FakeApi.Id1;
        Assert.Equal(
            [p + "/orders", p + "/orders/1/cancel", p + "/orders/1/modify", p + "/positions/2/modify", p + "/positions/2/close",
             p + "/positions/2/close-by", p + "/positions/2/close-partial", p + "/positions/2/trailing-stop", p + "/positions/2/trailing-stop"],
            api.Requests.Select(r => r.PathAndQuery));
        Assert.Equal(Enumerable.Range(0, 9).Select(i => "k-" + i), api.Requests.Select(r => r.Key));
        Assert.Equal("""{"symbol":"EURUSD","side":"buy","order_type":"market","volume":"0.10","magic":7}""", api.Requests[0].Body);
        Assert.Equal("""{"volume":"0.05"}""", api.Requests[6].Body);
    }

    /// <summary>
    /// Given a venue that refuses a trailing stop, and one that is unavailable / When each command is sent
    /// / Then each is a result that is not ok, never an exception, and was sent once.
    /// </summary>
    [Fact]
    public async Task A_venues_refusal_is_a_result()
    {
        using var api = new FakeApi()
            .Answer("""{"command_id": "0191f0c2-aaaa-7bbb-8ccc-000000000002", "state": "REJECTED", "final": true, "result": {"ok": false, "reason": "not_on_this_venue", "venue": "ctrader", "venue_code": "TRAILING_NOT_SUPPORTED"}}""")
            .Fail(503, "upstream_error", "the venue did not answer");
        using var client = api.Client();

        var refused = await client.TradingAccounts.SetTrailingStopAsync(Account, 2, 0.001m, "k-1");
        var down = await client.TradingAccounts.ClosePositionAsync(Account, 2, "k-2");

        Assert.Equal((false, Reason.NotOnThisVenue, "TRAILING_NOT_SUPPORTED", false), (refused.Ok, refused.Reason, refused.VenueCode, refused.Retryable));
        Assert.Equal((false, Reason.Refused), (down.Ok, down.Reason));
        Assert.Equal(2, api.Requests.Count);
    }
}

/// <summary>The cTrader grant flow.</summary>
public sealed class CtraderGrantTests
{
    private static readonly Guid GrantId = Guid.Parse(FakeApi.Id2);

    private static string Grant(string state) =>
        $$"""{"id": "{{FakeApi.Id2}}", "environment": "demo", "scope": "trading", "state": "{{state}}", "created_at": "2026-11-17T09:00:00Z", "accounts": []}""";

    /// <summary>
    /// Given a return URL and the caller's state / When a grant is started / Then one POST of
    /// /accounts/ctrader/grants carries the environment, scope, URL and state in the contract's words with
    /// the key, and the consent link comes back; a URL that is not an absolute web address is refused before sending.
    /// </summary>
    [Fact]
    public async Task A_grant_is_started_with_its_return_url_and_the_callers_state()
    {
        using var api = new FakeApi().Answer(
            $$"""{"grant_id": "{{FakeApi.Id2}}", "consent_url": "https://id.ctrader.com/consent?x=1", "expires_at": "2026-11-17T09:15:00Z"}""", 201);
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.TradingAccounts.StartCtraderGrantAsync(
            CtraderGrantCreateEnvironment.Demo, CtraderGrantCreateScope.Trading, "/back", "k"));
        var started = await client.TradingAccounts.StartCtraderGrantAsync(
            CtraderGrantCreateEnvironment.Demo, CtraderGrantCreateScope.Trading, "https://app.example.test/back", "grant-1", tenantState: "u-42");

        Assert.Equal(GrantId, started.GrantId);
        var sent = api.Only;
        Assert.Equal(("POST", "/v1/accounts/ctrader/grants", "grant-1"), (sent.Method, sent.PathAndQuery, sent.Key));
        Assert.Equal(("demo", "trading", "https://app.example.test/back", "u-42"),
            (sent.Json!["environment"]!.GetValue<string>(), sent.Json["scope"]!.GetValue<string>(), sent.Json["return_url"]!.GetValue<string>(), sent.Json["tenant_state"]!.GetValue<string>()));
    }

    /// <summary>
    /// Given an ACTIVE grant / When it is read, two of its accounts are connected and it is revoked / Then
    /// the ids go as cTrader's strings, the accounts come back as a list, the revoke is a DELETE with the
    /// key, and connecting none is refused before sending.
    /// </summary>
    [Fact]
    public async Task A_grants_accounts_are_connected_and_the_grant_revoked()
    {
        using var api = new FakeApi().Answer(Grant("ACTIVE"))
            .Answer($$"""{"items": [{"id": "{{FakeApi.Id1}}", "venue": "ctrader", "state": "PENDING", "broker": {}, "runners": [], "created_at": "2026-11-17T09:00:00Z"}]}""", 201)
            .Answer(Grant("REVOKED"));
        using var client = api.Client();
        var t = client.TradingAccounts;

        await Assert.ThrowsAsync<ArgumentException>(() => t.ConnectCtraderAccountsAsync(GrantId, [], "k"));
        var grant = await t.CtraderGrantAsync(GrantId);
        var connected = await t.ConnectCtraderAccountsAsync(GrantId, ["4401", "4402"], "connect-1");
        var revoked = await t.RevokeCtraderGrantAsync(GrantId, "revoke-1");

        Assert.Equal(CtraderGrantState.Active, grant.State);
        Assert.Equal(TradingAccountState.Pending, Assert.Single(connected).State);
        Assert.Equal(CtraderGrantState.Revoked, revoked.State);
        var p = "/v1/accounts/ctrader/grants/" + FakeApi.Id2;
        Assert.Equal([("GET", p, null), ("POST", p + "/accounts", "connect-1"), ("DELETE", p, "revoke-1")],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        Assert.Equal("""{"ctid_trader_account_ids":["4401","4402"]}""", api.Requests[1].Body);
    }
}

/// <summary>
/// Following an account live, snapshot then marks:
/// what is applied.
/// </summary>
public sealed class LiveAccountTests
{
    internal static readonly Guid TerminalId = Guid.Parse(FakeApi.Id1);
    internal static readonly string Channel = MfxHub.Channel.Account(TerminalId);

    internal static StreamMessage Message(string type, long? seq, string data, long? @base = null) =>
        new(Channel, seq, type, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data)!, @base);

    internal static StreamMessage SnapshotAt(long seq) => Message(LiveAccount.Snapshot, seq, $$"""
        {"terminal_id": "{{FakeApi.Id1}}", "as_of": "t{{seq}}", "balance": "1000.00", "margin_free": "900.00", "open_positions_count": 2,
         "positions": [{"ticket": 11, "symbol": "EURUSD", "profit": "1.00"}, {"ticket": "12", "symbol": "XAUUSD", "profit": "-2.00"}],
         "pending": [{"ticket": 21}]}
        """);

    /// <summary>
    /// Given a snapshot and then a marks message on its base / When each is applied / Then the view is the
    /// snapshot's, keyed by ticket in its order, with every figure by the name it arrived under; the mark
    /// sets price_current, profit and swap on its position by position, and the figures it carries
    /// replace the held ones while the rest stay.
    /// </summary>
    [Fact]
    public void A_snapshot_is_drawn_and_its_marks_applied()
    {
        var live = new LiveAccount(TerminalId);
        Assert.Null(live.View);

        var drawn = live.Apply(SnapshotAt(5))!;
        var marked = live.Apply(Message(LiveAccount.Marks, 6, """{"as_of": "t6", "equity": "1003.00", "marks": [[12, "2401.5", "3.00", "-0.10"]]}""", @base: 5))!;

        Assert.Equal([11L, 12L], drawn.Positions.Keys);
        Assert.Equal((5L, 5L), (drawn.Seq, drawn.Base));
        Assert.Equal(["balance", "margin_free"], drawn.Figures.Keys.Order(StringComparer.Ordinal));
        Assert.Single(drawn.Pending);
        Assert.Equal(("t6", 6L, 5L), (marked.AsOf, marked.Seq, marked.Base));
        Assert.Equal(("2401.5", "3.00", "-0.10", "XAUUSD"),
            (marked.Positions[12]["price_current"].GetString(), marked.Positions[12]["profit"].GetString(), marked.Positions[12]["swap"].GetString(), marked.Positions[12]["symbol"].GetString()));
        Assert.Equal("1.00", marked.Positions[11]["profit"].GetString());
        Assert.Equal(("1003.00", "1000.00"), (marked.Figures["equity"].GetString(), marked.Figures["balance"].GetString()));
        Assert.False(live.Behind);
    }

    /// <summary>
    /// Given a view / When an empty marks list arrives, then a message for another terminal, another kind
    /// and another channel / Then the empty list is a keep-alive that applies, and the others are ignored
    /// without falling behind.
    /// </summary>
    [Fact]
    public void A_keep_alive_applies_and_anothers_messages_are_ignored()
    {
        var live = new LiveAccount(TerminalId);
        live.Apply(SnapshotAt(5));

        var kept = live.Apply(Message(LiveAccount.Marks, 6, """{"marks": []}"""));
        var other = live.Apply(Message(LiveAccount.Marks, 7, $$"""{"terminal_id": "{{FakeApi.Id2}}", "marks": [[11, "1", "1", "1"]]}"""));
        var kind = live.Apply(Message("account.something_else", 7, "{}"));
        var channel = live.Apply(new StreamMessage(MfxHub.Channel.Account(Guid.Parse(FakeApi.Id3)), 7, LiveAccount.Marks,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"marks": [[11, "1", "1", "1"]]}""")!));

        Assert.NotNull(kept);
        Assert.Equal(6L, kept.Seq);
        Assert.Null(other);
        Assert.Null(kind);
        Assert.Null(channel);
        Assert.False(live.Behind);
        Assert.Equal("1.00", live.View!.Positions[11]["profit"].GetString());
    }
}

/// <summary>Following an account live: what is refused, and how the view catches up.</summary>
public sealed class LiveAccountBehindTests
{
    /// <summary>
    /// Given a view at seq 5 / When a mark arrives with a gap, another base, an unknown ticket, or a mark
    /// that is not four elements / Then each is dropped whole and the view falls behind, unchanged; and a
    /// mark before any snapshot falls behind too.
    /// </summary>
    [Theory]
    [InlineData(7, 5, """[[11, "1", "1", "1"]]""")]
    [InlineData(6, 4, """[[11, "1", "1", "1"]]""")]
    [InlineData(6, 5, """[[99, "1", "1", "1"]]""")]
    [InlineData(6, 5, """[[11, "1", "1"]]""")]
    [InlineData(6, 5, """[{"ticket": 11}]""")]
    public void A_mark_that_cannot_apply_is_dropped_whole_and_the_view_falls_behind(long seq, long @base, string marks)
    {
        var fresh = new LiveAccount(LiveAccountTests.TerminalId);
        Assert.Null(fresh.Apply(LiveAccountTests.Message(LiveAccount.Marks, 1, """{"marks": []}""")));
        Assert.True(fresh.Behind);

        var live = new LiveAccount(LiveAccountTests.TerminalId);
        var before = live.Apply(LiveAccountTests.SnapshotAt(5));

        var applied = live.Apply(LiveAccountTests.Message(LiveAccount.Marks, seq, $$"""{"marks": {{marks}}}""", @base));

        Assert.Null(applied);
        Assert.True(live.Behind);
        Assert.Same(before, live.View);
    }

    /// <summary>
    /// Given a view that is behind / When a resync arrives, then a snapshot with a smaller seq than held /
    /// Then the resync drops the view, and the snapshot draws it again and clears behind, whatever its seq;
    /// a refusal of the channel drops the view as well.
    /// </summary>
    [Fact]
    public void A_snapshot_catches_up_and_a_resync_or_refusal_drops_the_view()
    {
        var live = new LiveAccount(LiveAccountTests.TerminalId);
        live.Apply(LiveAccountTests.SnapshotAt(50));

        Assert.Null(live.Apply(new Resync(LiveAccountTests.Channel)));
        Assert.Null(live.View);
        Assert.True(live.Behind);
        var again = live.Apply(LiveAccountTests.SnapshotAt(2));
        Assert.NotNull(again);
        Assert.Equal(2L, again.Seq);
        Assert.False(live.Behind);

        live.Apply(new ChannelRefused(LiveAccountTests.Channel, "not_found", "gone", new Dictionary<string, JsonElement>()));
        Assert.Null(live.View);
        Assert.True(live.Behind);
    }
}
