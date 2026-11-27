using System.Text.Json;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>Market-data subscriptions.</summary>
public sealed class MarketDataSubscriptionTests
{
    private static readonly Guid Server = Guid.Parse(FakeApi.Id1);
    private static readonly Guid Account = Guid.Parse(FakeApi.Id2);

    private static string Subscription(string id) =>
        $$"""{"id": "{{id}}", "broker": {"id": "{{id}}"}, "server": {"id": "{{id}}"}, "symbol": "EURUSD.sd", "timeframes": ["M1"], "delivery": "ws", "state": "streaming", "created_at": "2026-11-17T09:00:00Z"}""";

    /// <summary>
    /// Given a server, a symbol in the broker's name and two timeframes / When it is subscribed / Then one
    /// POST of /md/subscriptions carries them with ws delivery and the key; a timeframe that is not one of
    /// the nine is refused before sending, and unsubscribing is a DELETE with its key.
    /// </summary>
    [Fact]
    public async Task A_symbol_is_subscribed_with_its_timeframes_and_unsubscribed()
    {
        using var api = new FakeApi().Answer(Subscription(FakeApi.Id3), 201).Answer("", 204);
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.Md.SubscribeAsync(Server, "EURUSD.sd", "k", ["M2"]));
        var subscription = await client.Md.SubscribeAsync(Server, "EURUSD.sd", "sub-1", ["M1", "H1"]);
        await client.Md.UnsubscribeAsync(subscription.Id, "unsub-1");

        var sent = api.Requests[0];
        Assert.Equal(("POST", "/v1/md/subscriptions", "sub-1"), (sent.Method, sent.PathAndQuery, sent.Key));
        Assert.Equal($$"""{"server_id":"{{FakeApi.Id1}}","symbol":"EURUSD.sd","timeframes":["M1","H1"],"delivery":"ws"}""", sent.Body);
        Assert.Equal(("DELETE", "/v1/md/subscriptions/" + FakeApi.Id3, "unsub-1"), (api.Requests[1].Method, api.Requests[1].PathAndQuery, api.Requests[1].Key));
    }

    /// <summary>
    /// Given models that hold lists of the contract's enums (a farm's usages, a window's suspended
    /// services) / When they are read and written / Then each member is the contract's word both ways, and
    /// an enum the contract writes as a number (a farm's refresh rate) stays a number.
    /// </summary>
    [Fact]
    public void A_list_of_the_contracts_enums_goes_as_words()
    {
        var farm = JsonSerializer.Deserialize<TenantFarm>(
            $$"""{"id": "{{FakeApi.Id1}}", "name": "f", "os": "windows", "region": "r", "usages": ["order_execution", "quote_streaming"], "redundancy": "none", "refresh_ms": 1000, "instruments_max": 5, "status": "active"}""",
            MfxJson.Options)!;
        var window = JsonSerializer.Deserialize<TenantMaintenance>(
            $$"""{"id": "{{FakeApi.Id1}}", "label": "l", "state": "scheduled", "starts_at": "2026-11-20T02:00:00Z", "ends_at": "2026-11-20T03:00:00Z", "expected_minutes": 60, "suspended": ["order_placement"], "terminals": 1}""",
            MfxJson.Options)!;

        var written = JsonSerializer.SerializeToNode(farm, MfxJson.Options)!;
        Assert.Equal(2, farm.Usages.Count);
        Assert.Equal("order_execution", written["usages"]![0]!.GetValue<string>());
        Assert.Equal(1000, written["refresh_ms"]!.GetValue<int>());
        Assert.Equal("order_placement", JsonSerializer.SerializeToNode(window, MfxJson.Options)!["suspended"]![0]!.GetValue<string>());
    }

    /// <summary>
    /// Given a cTrader account / When a symbol is subscribed on it and its subscriptions are walked / Then
    /// the subscription body is the symbol alone, it comes back pending, and the walk follows the cursor;
    /// the tenant's own subscriptions walk the same way.
    /// </summary>
    [Fact]
    public async Task An_account_subscription_is_a_pending_resource_and_the_lists_follow_the_cursor()
    {
        var accountSub = $$"""{"id": "{{FakeApi.Id3}}", "account_id": "{{FakeApi.Id2}}", "symbol": "EURUSD", "state": "pending", "created_at": "2026-11-17T09:00:00Z"}""";
        using var api = new FakeApi().Answer(accountSub, 201).Answer(FakeApi.PageOf("c-2", accountSub)).Answer(FakeApi.PageOf(null, accountSub))
            .Answer(FakeApi.PageOf(null, Subscription(FakeApi.Id1)));
        using var client = api.Client();

        var pending = await client.Md.SubscribeAccountAsync(Account, "EURUSD", "acct-sub-1");
        var count = 0;
        await foreach (var _ in client.Md.AllAccountSubscriptionsAsync(Account))
        {
            count++;
        }
        await foreach (var _ in client.Md.AllSubscriptionsAsync())
        {
            count++;
        }

        Assert.Equal(AccountMdSubscriptionState.Pending, pending.State);
        Assert.Equal("""{"symbol":"EURUSD"}""", api.Requests[0].Body);
        Assert.Equal(3, count);
        var p = $"/v1/accounts/{FakeApi.Id2}/md/subscriptions";
        Assert.Equal([p, p, p + "?cursor=c-2", "/v1/md/subscriptions"], api.Requests.Select(r => r.PathAndQuery));
    }
}

/// <summary>Candle and tick history.</summary>
public sealed class MarketDataHistoryTests
{
    private static readonly Guid Broker = Guid.Parse(FakeApi.Id1);
    private static readonly DateTimeOffset From = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Given a window / When a broker's candles and a cTrader account's candles are read / Then each is one
    /// GET with the symbol, tf, from and to (and the server when named), the bars keep their prices exactly,
    /// and a timeframe that is not one of the nine is refused before sending.
    /// </summary>
    [Fact]
    public async Task Candles_are_read_for_a_window_by_broker_or_by_account()
    {
        var bars = """{"items": [{"ts": "2026-11-12T09:00:00Z", "o": "1.15800", "h": "1.1600", "l": "1.1570", "c": "1.1597", "v": 120}]}""";
        using var api = new FakeApi().Answer(bars).Answer(bars);
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.Md.CandlesAsync(Broker, "EURUSD", "H2", From, From.AddHours(2)));
        var candles = await client.Md.CandlesAsync(Broker, "EURUSD", "H1", From, From.AddHours(2), limit: 10, serverId: Guid.Parse(FakeApi.Id2));
        await client.Md.AccountCandlesAsync(Guid.Parse(FakeApi.Id3), "EURUSD", "M5", From, From.AddHours(2));

        Assert.Equal("1.15800", Assert.Single(candles.Items).O);
        Assert.Equal(
            [
                $"/v1/md/candles?broker={FakeApi.Id1}&symbol=EURUSD&tf=H1&from=2026-11-12T09%3A00%3A00%2B00%3A00&to=2026-11-12T11%3A00%3A00%2B00%3A00&limit=10&server_id={FakeApi.Id2}",
                $"/v1/accounts/{FakeApi.Id3}/md/candles?symbol=EURUSD&tf=M5&from=2026-11-12T09%3A00%3A00%2B00%3A00&to=2026-11-12T11%3A00%3A00%2B00%3A00",
            ],
            api.Requests.Select(r => r.PathAndQuery));
    }

    private static string Tick(string ts, string bid) => $$"""{"ts": "{{ts}}", "bid": "{{bid}}", "ask": "1.2"}""";

    private static string Ticks(bool more, params string[] ticks) => "{\"items\": [" + string.Join(",", ticks) + "], \"more\": " + (more ? "true" : "false") + "}";

    /// <summary>
    /// Given three ticks sharing one millisecond at the end of a page, and the next page starting with two
    /// of them again / When every tick of the window is read / Then each next call starts at the last
    /// tick's time, the ticks already held are dropped exactly once each, the one that was not held is
    /// kept, and the walk ends when more is false.
    /// </summary>
    [Fact]
    public async Task Every_tick_is_read_once_across_a_shared_millisecond()
    {
        using var api = new FakeApi()
            .Answer(Ticks(true, Tick("2026-11-12T09:00:00.001Z", "1.1"), Tick("2026-11-12T09:00:00.002Z", "1.2"), Tick("2026-11-12T09:00:00.002Z", "1.3")))
            .Answer(Ticks(false, Tick("2026-11-12T09:00:00.002Z", "1.2"), Tick("2026-11-12T09:00:00.002Z", "1.3"), Tick("2026-11-12T09:00:00.002Z", "1.4"), Tick("2026-11-12T09:00:00.003Z", "1.5")));
        using var client = api.Client();

        var bids = new List<string>();
        await foreach (var tick in client.Md.AllTicksAsync(Broker, "EURUSD", From, pageSize: 3))
        {
            bids.Add(tick.Bid);
        }

        Assert.Equal(["1.1", "1.2", "1.3", "1.4", "1.5"], bids);
        Assert.Equal(
            [
                $"/v1/md/ticks?broker={FakeApi.Id1}&symbol=EURUSD&from=2026-11-12T09%3A00%3A00%2B00%3A00&limit=3",
                $"/v1/md/ticks?broker={FakeApi.Id1}&symbol=EURUSD&from=2026-11-12T09%3A00%3A00.002000%2B00%3A00&limit=3",
            ],
            api.Requests.Select(r => r.PathAndQuery));
    }

    /// <summary>
    /// Given a page that promises more but brings nothing new / When the ticks are walked / Then the walk
    /// stops rather than asking for the same ticks for ever.
    /// </summary>
    [Fact]
    public async Task A_page_with_nothing_new_ends_the_walk()
    {
        var same = Tick("2026-11-12T09:00:00.002Z", "1.2");
        using var api = new FakeApi().Answer(Ticks(true, same)).Answer(Ticks(true, same));
        using var client = api.Client();

        var count = 0;
        await foreach (var _ in client.Md.AllTicksAsync(Broker, "EURUSD", From))
        {
            count++;
        }

        Assert.Equal(1, count);
        Assert.Equal(2, api.Requests.Count);
    }
}

/// <summary>
/// The typed stream events: the order,
/// position, terminal and candle events.
/// </summary>
public sealed class StreamEventsTests
{
    private static StreamMessage Message(string? type, string data, string channel = "events") =>
        new(channel, 1, type, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data)!);

    private const string Order = $$"""
        "schema_version": "1", "terminal_id": "{{FakeApi.Id1}}", "event_id": "e-1", "ticket": "5001", "symbol": "EURUSD", "side": "buy",
        "order_type": "stop_limit", "volume": "0.10", "price": "1.1000", "stop_limit_price": "1.0990", "sl": null, "tp": null,
        "placed_time": "2026-11-17T09:00:00Z", "expires_at": null, "magic": "42", "comment": "", "ts": "2026-11-17T09:00:01.250Z"
        """;

    /// <summary>
    /// Given an order.closed whose every value arrives as text / When it is read / Then its ids are Guids,
    /// its tickets and magic numbers longs, its prices exact decimals, its times instants, its words text,
    /// an absent or null optional field null, and the position it became is named.
    /// </summary>
    [Fact]
    public void An_order_event_reads_from_text_into_its_types()
    {
        var closed = Assert.IsType<OrderClosed>(MfxEvent.Of(Message(OrderClosed.Type,
            "{" + Order + """, "reason": "filled", "position_ticket": "7001", "idempotency_key": "k-9"}""")));

        Assert.Equal(("order.closed", Guid.Parse(FakeApi.Id1), 5001L, 42L), (closed.Kind, closed.TerminalId, closed.Ticket, closed.Magic));
        Assert.Equal((0.10m, 1.1000m, (decimal?)1.0990m, (decimal?)null), (closed.Volume, closed.Price, closed.StopLimitPrice, closed.Tp));
        Assert.Equal("0.10", closed.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(("stop_limit", "filled", (long?)7001, "k-9"), (closed.OrderType, closed.Reason, closed.PositionTicket, closed.IdempotencyKey));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 9, 0, 1, 250, TimeSpan.Zero), closed.Ts);
        Assert.Null(closed.ExpiresAt);
        Assert.IsType<OrderPlaced>(MfxEvent.Of(Message(OrderPlaced.Type, "{" + Order + "}")));
        Assert.IsType<OrderModified>(MfxEvent.Of(Message(OrderModified.Type, "{" + Order + "}")));
    }

    /// <summary>
    /// Given the position, terminal and candle events / When each is read / Then each is its type with its
    /// fields, the terminal's from and to become FromState and ToState, and a candle is read by its channel
    /// although it carries no event type.
    /// </summary>
    [Fact]
    public void Position_terminal_and_candle_events_read_as_their_types()
    {
        const string position = $$"""
            "schema_version": "1", "terminal_id": "{{FakeApi.Id1}}", "event_id": "e-2", "ticket": "7001", "symbol": "EURUSD", "side": "sell",
            "volume": "0.10", "open_price": "1.0990", "price_current": "1.0985", "profit": "5.00", "swap": "0", "open_time": "2026-11-17T09:00:00Z",
            "magic": "0", "comment": "", "ts": "2026-11-17T10:00:00Z"
            """;
        var opened = Assert.IsType<PositionOpened>(MfxEvent.Of(Message(PositionOpened.Type, "{" + position + ", \"order_ticket\": \"5001\"}")));
        var closed = Assert.IsType<PositionClosed>(MfxEvent.Of(Message(PositionClosed.Type, "{" + position + ", \"close_price\": \"1.0985\", \"close_reason\": \"tp\"}")));
        Assert.IsType<PositionModified>(MfxEvent.Of(Message(PositionModified.Type, "{" + position + "}")));
        var moved = Assert.IsType<TerminalStateChanged>(MfxEvent.Of(Message(TerminalStateChanged.Type,
            $$"""{"schema_version": "1", "terminal_id": "{{FakeApi.Id1}}", "from": "ACTIVE", "to": "OFFLINE", "reason": "cell_silent", "transition_id": "{{FakeApi.Id2}}", "ts": "2026-11-17T10:00:00Z"}""")));
        var bar = Assert.IsType<CandleClosed>(MfxEvent.Of(Message(null,
            $$"""{"schema_version": "1", "server_id": "{{FakeApi.Id1}}", "symbol": "EURUSD", "timeframe": "M1", "ts": "2026-11-17T10:00:00Z", "o": "1.1", "h": "1.2", "l": "1.0", "c": "1.15", "v": "17", "prev_ts": "2026-11-17T09:59:00Z"}""",
            $"candles:{FakeApi.Id1}:EURUSD:M1")));

        Assert.Equal(((long?)5001, 5.00m), (opened.OrderTicket, opened.Profit!.Value));
        Assert.Equal(("tp", (decimal?)1.0985m), (closed.CloseReason, closed.ClosePrice));
        Assert.Equal(("ACTIVE", "OFFLINE"), (moved.FromState, moved.ToState));
        Assert.Equal((1.15m, 17L, "M1"), (bar.C, bar.V, bar.Timeframe));
    }

    /// <summary>
    /// Given a message of a type this version does not read, a typeless message off a candles channel, and
    /// an order event that lacks a required field / When each is read / Then the first two are null, and
    /// the third is a FormatException naming the field.
    /// </summary>
    [Fact]
    public void Another_type_is_null_and_a_missing_field_is_named()
    {
        Assert.Null(MfxEvent.Of(Message("something.new", "{}")));
        Assert.Null(MfxEvent.Of(Message(null, "{}", $"md:{FakeApi.Id1}:EURUSD")));
        Assert.Contains("order.placed", MfxEvent.Kinds);
        var missing = Assert.Throws<FormatException>(() => MfxEvent.Of(Message(OrderPlaced.Type, "{" + Order.Replace("\"ticket\": \"5001\",", "", StringComparison.Ordinal) + "}")));
        Assert.Contains("`ticket`", missing.Message, StringComparison.Ordinal);
    }
}

/// <summary>The order book and the subscription-health events.</summary>
public sealed class BookAndHealthTests
{
    private static StreamMessage Message(string channel, string? type, string data) =>
        new(channel, 3, type, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data)!);

    /// <summary>
    /// Given a book message / When it is read / Then each side is (price, volume) pairs as exact decimals in
    /// the platform's order, the best bid and ask are the first of each, an empty side has none, and any
    /// other message is null.
    /// </summary>
    [Fact]
    public void A_book_reads_with_exact_levels_and_its_best_prices()
    {
        var channel = Channel.Book(Guid.Parse(FakeApi.Id1), "XAUUSD");
        var book = MarketData.BookOf(Message(channel, MarketData.BookMessage,
            """{"bids": [["2400.10", "1.5"], ["2400.00", "3"]], "asks": [], "broker_ts": "2026-11-17 12:00:00.123", "levels": "10"}"""))!;

        Assert.Equal([(2400.10m, 1.5m), (2400.00m, 3m)], book.Bids);
        Assert.Equal((2400.10m, (decimal?)null, 10, 3L), (book.BestBid!.Value, book.BestAsk, book.Levels, book.Seq!.Value));
        Assert.Equal("2400.10", book.BestBid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(MarketData.BookOf(Message(channel, "md.tick", "{}")));
    }

    /// <summary>
    /// Given a degraded and a recovered message on events / When each is read / Then each is its record with
    /// its ids, symbol, reason and instants, a never-ticked symbol's last tick null, and any other message null.
    /// </summary>
    [Fact]
    public void Subscription_health_reads_as_degraded_or_recovered()
    {
        var ids = "\"subscription_id\": \"" + FakeApi.Id1 + "\", \"broker_id\": \"" + FakeApi.Id2 + "\", \"server_id\": \"" + FakeApi.Id3 + "\", \"symbol\": \"EURUSD\", \"reason\": \"report_stale\", \"since\": \"2026-11-17T09:00:00Z\"";
        var degraded = Assert.IsType<SubscriptionDegraded>(MarketData.SubscriptionEvent(Message("events", MarketData.Degraded, "{" + ids + "}")));
        var recovered = Assert.IsType<SubscriptionRecovered>(MarketData.SubscriptionEvent(Message("events", MarketData.Recovered,
            "{" + ids + ", \"recovered_at\": \"2026-11-17T09:05:00Z\", \"last_tick_at\": \"2026-11-17T09:05:00Z\"}")));

        Assert.Equal(("report_stale", (DateTimeOffset?)null), (degraded.Reason, degraded.LastTickAt));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 9, 5, 0, TimeSpan.Zero), recovered.RecoveredAt);
        Assert.Null(MarketData.SubscriptionEvent(Message("events", "terminal.state_changed", "{}")));
    }
}
