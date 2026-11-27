using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MfxHub.Models;

namespace MfxHub;

/// <summary>
/// A subscription whose symbol cannot tick, or should be ticking and is not (<c>md.subscription_degraded</c>).
/// Sent once, when it becomes degraded. <see cref="Reason"/> is the platform's own word, not a closed list.
/// </summary>
/// <param name="SubscriptionId">The subscription.</param>
/// <param name="BrokerId">Its broker.</param>
/// <param name="ServerId">Its server.</param>
/// <param name="Symbol">Its symbol.</param>
/// <param name="Reason">Why, in the platform's word (<c>symbol_removed</c>, <c>terminal_disconnected</c> and others).</param>
/// <param name="Since">When it became degraded.</param>
/// <param name="LastTickAt">The symbol's last tick; null when it has never ticked.</param>
[ContractSchema("MdSubscriptionDegraded")]
public sealed record SubscriptionDegraded(
    Guid SubscriptionId, Guid BrokerId, Guid ServerId, string Symbol, string Reason, DateTimeOffset Since, DateTimeOffset? LastTickAt);

/// <summary>A degraded subscription that is delivering again (<c>md.subscription_recovered</c>). Sent once.</summary>
/// <param name="SubscriptionId">The subscription.</param>
/// <param name="BrokerId">Its broker.</param>
/// <param name="ServerId">Its server.</param>
/// <param name="Symbol">Its symbol.</param>
/// <param name="Reason">What it recovered from.</param>
/// <param name="Since">When it had become degraded.</param>
/// <param name="RecoveredAt">When it recovered.</param>
/// <param name="LastTickAt">The symbol's last tick.</param>
[ContractSchema("MdSubscriptionRecovered")]
public sealed record SubscriptionRecovered(
    Guid SubscriptionId, Guid BrokerId, Guid ServerId, string Symbol, string Reason, DateTimeOffset Since, DateTimeOffset RecoveredAt,
    DateTimeOffset? LastTickAt);

/// <summary>
/// A symbol's whole order book as one terminal read it (<c>md.book</c> on <c>book:{server_id}:{symbol}</c>).
/// Replace yours with every one. Prices and volumes are exact decimals. Not a reconstruction: MFX Hub
/// builds no book from ticks or from several terminals.
/// </summary>
/// <param name="Channel">The channel it arrived on.</param>
/// <param name="Seq">Its seq; a gap is a book superseded before it arrived, not a lost fact.</param>
/// <param name="Bids">Buy orders, highest price first.</param>
/// <param name="Asks">Sell orders, lowest price first.</param>
/// <param name="BrokerTs">When the terminal read it, on the broker's clock.</param>
/// <param name="Levels">The most a side the platform reads: a shorter side is the broker's whole side.</param>
[ContractSchema("BookMessage")]
public sealed record Book(
    string Channel, long? Seq, IReadOnlyList<(decimal Price, decimal Volume)> Bids, IReadOnlyList<(decimal Price, decimal Volume)> Asks,
    string BrokerTs, int Levels)
{
    /// <summary>The highest buy price, or null when the book has no bids.</summary>
    public decimal? BestBid => Bids.Count > 0 ? Bids[0].Price : null;

    /// <summary>The lowest sell price, or null when the book has no asks.</summary>
    public decimal? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;
}

/// <summary>What this tenant has asked to be streamed, and the market-data reads (<c>client.Md</c>).</summary>
public sealed class MarketData
{
    /// <summary>The message on <c>events</c> that says a subscription stopped delivering.</summary>
    public const string Degraded = "md.subscription_degraded";

    /// <summary>The message on <c>events</c> that says a subscription delivers again.</summary>
    public const string Recovered = "md.subscription_recovered";

    /// <summary>The message on <c>book:{server_id}:{symbol}</c>.</summary>
    public const string BookMessage = "md.book";

    /// <summary>The timeframes a candle read takes.</summary>
    public static readonly IReadOnlyList<string> Timeframes = ["M1", "M5", "M15", "M30", "H1", "H4", "D1", "W1", "MN1"];

    private readonly MfxClient _client;

    internal MarketData(MfxClient client) => _client = client;

    /// <summary>A message from a <c>book:</c> channel as the <see cref="Book"/> it is, or null for any other.</summary>
    /// <param name="message">A message from the stream.</param>
    /// <returns>The book, or null.</returns>
    /// <exception cref="FormatException">The message is a book that is not the contract's shape.</exception>
    public static Book? BookOf(StreamMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type != BookMessage)
        {
            return null;
        }
        var data = new EventData(BookMessage, message.Data);
        return new Book(message.Channel, message.Seq, Side(data.Raw("bids", true)), Side(data.Raw("asks", true)),
            data.Text("broker_ts"), (int)data.Integer("levels"));
    }

    private static (decimal, decimal)[] Side(JsonElement side) =>
        side.ValueKind != JsonValueKind.Array
            ? throw new FormatException("a book side is a list of [price, volume]")
            : [.. side.EnumerateArray().Select(level => (EventData.Exact(level[0]), EventData.Exact(level[1])))];

    /// <summary>
    /// A message from <c>events</c> as the subscription-health event it is, or null for any other. A
    /// subscription keeps its stream while degraded: nothing needs subscribing again when it recovers.
    /// </summary>
    /// <param name="message">A message from the stream.</param>
    /// <returns>A <see cref="SubscriptionDegraded"/>, a <see cref="SubscriptionRecovered"/>, or null.</returns>
    /// <exception cref="FormatException">The message lacks a field the contract requires.</exception>
    public static object? SubscriptionEvent(StreamMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type is not (Degraded or Recovered))
        {
            return null;
        }
        var d = new EventData(message.Type, message.Data);
        var (id, broker, server) = (d.Uuid("subscription_id"), d.Uuid("broker_id"), d.Uuid("server_id"));
        var (symbol, reason, since, lastTick) = (d.Text("symbol"), d.Text("reason"), d.Instant("since"), d.InstantOrNull("last_tick_at"));
        return message.Type == Degraded
            ? new SubscriptionDegraded(id, broker, server, symbol, reason, since, lastTick)
            : new SubscriptionRecovered(id, broker, server, symbol, reason, since, d.Instant("recovered_at"), lastTick);
    }

    /// <summary>
    /// Asks for a symbol's ticks on one broker server, and optionally its candles. The symbol is the
    /// broker's own name as the catalogue lists it (<c>EURUSD.sd</c>). Candles arrive on
    /// <c>candles:{server_id}:{symbol}:{tf}</c>. The same key returns the same subscription.
    /// </summary>
    /// <param name="serverId">The broker server.</param>
    /// <param name="symbol">The broker's name for it.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="timeframes">The candles to build; none is ticks only.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The subscription.</returns>
    public Task<MdSubscription> SubscribeAsync(
        Guid serverId, string symbol, string idempotencyKey, IEnumerable<string>? timeframes = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var frames = timeframes?.Select(t => Wire.Parse<Models.Timeframes>(t)
            ?? throw new ArgumentException($"'{t}' is not one of {string.Join(", ", Timeframes)}", nameof(timeframes))).ToList();
        var body = new MdSubscriptionCreate
        {
            ServerId = serverId,
            Symbol = symbol,
            Timeframes = frames is { Count: > 0 } ? frames : null,
            Delivery = MdSubscriptionCreateDelivery.Ws,
        };
        return _client.WriteAsync<MdSubscription>(HttpMethod.Post, "/md/subscriptions", body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>One page of what this tenant has subscribed, newest first.</summary>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<MdSubscription>> SubscriptionsAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.PageAsync<MdSubscription>("/md/subscriptions", new Query().Add("cursor", cursor).Add("limit", limit), cancellationToken);

    /// <summary>Every subscription, following the cursor.</summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every subscription, one request a page.</returns>
    public IAsyncEnumerable<MdSubscription> AllSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        Pages.All((cursor, token) => SubscriptionsAsync(cursor, null, token), cancellationToken);

    /// <summary>
    /// Asks for a symbol's spots on one of the tenant's cTrader accounts: the counterpart of
    /// <see cref="SubscribeAsync"/>, keyed on the account. A resource, not a command: it exists at once
    /// and is <c>pending</c> until the first spot. One per account and symbol: asking again answers the
    /// live one.
    /// </summary>
    /// <param name="accountId">The cTrader account.</param>
    /// <param name="symbol">The account's own name for it.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The subscription.</returns>
    /// <exception cref="StateException">A terminal account (<c>not_on_this_venue</c>): it streams its server.</exception>
    public Task<AccountMdSubscription> SubscribeAccountAsync(
        Guid accountId, string symbol, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return _client.WriteAsync<AccountMdSubscription>(
            HttpMethod.Post, "/accounts/" + Wire.Id(accountId) + "/md/subscriptions", new AccountMdSubscriptionCreate { Symbol = symbol },
            Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>One page of an account's market-data subscriptions, newest first.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<AccountMdSubscription>> AccountSubscriptionsAsync(
        Guid accountId, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.PageAsync<AccountMdSubscription>(
            "/accounts/" + Wire.Id(accountId) + "/md/subscriptions", new Query().Add("cursor", cursor).Add("limit", limit), cancellationToken);

    /// <summary>Every subscription of an account, following the cursor.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every subscription, one request a page.</returns>
    public IAsyncEnumerable<AccountMdSubscription> AllAccountSubscriptionsAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        Pages.All((cursor, token) => AccountSubscriptionsAsync(accountId, cursor, null, token), cancellationToken);

    /// <summary>Stops streaming the symbol. The channel stops; nothing else is affected.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>When it is done.</returns>
    public Task UnsubscribeAsync(Guid subscriptionId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _client.WriteAsync(HttpMethod.Delete, "/md/subscriptions/" + Wire.Id(subscriptionId), null, Keys.Required(idempotencyKey), cancellationToken);

    /// <summary>
    /// A symbol's bars for a window, oldest first, at most 1,000 a call, read through one of the tenant's
    /// ACTIVE terminals on the broker's server. When the window holds more than <paramref name="limit"/>
    /// the answer is the newest of them; the bar still forming is included when the window reaches it.
    /// No bar in the window is an empty answer.
    /// </summary>
    /// <param name="broker">The broker.</param>
    /// <param name="symbol">The broker's name for it.</param>
    /// <param name="timeframe">One of <see cref="Timeframes"/>; any other is refused before sending.</param>
    /// <param name="from">The window's start.</param>
    /// <param name="to">The window's end.</param>
    /// <param name="limit">At most this many bars.</param>
    /// <param name="serverId">Which server, with terminals on more than one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bars.</returns>
    public Task<CandleList> CandlesAsync(
        Guid broker, string symbol, string timeframe, DateTimeOffset @from, DateTimeOffset to, int? limit = null, Guid? serverId = null,
        CancellationToken cancellationToken = default) =>
        _client.GetAsync<CandleList>("/md/candles",
            new Query().Add("broker", broker).Add("symbol", symbol).Add("tf", Frame(timeframe)).Add("from", @from).Add("to", to)
                .Add("limit", limit).Add("server_id", serverId),
            cancellationToken);

    /// <summary>
    /// A cTrader account's bars for a window, oldest first, at most 1,000 a call: the counterpart of
    /// <see cref="CandlesAsync"/>, keyed on the account; Spotware's trendbars in UTC.
    /// </summary>
    /// <param name="accountId">The cTrader account.</param>
    /// <param name="symbol">The account's own name for it.</param>
    /// <param name="timeframe">One of <see cref="Timeframes"/>.</param>
    /// <param name="from">The window's start.</param>
    /// <param name="to">The window's end.</param>
    /// <param name="limit">At most this many bars.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bars.</returns>
    /// <exception cref="StateException">A terminal account (<c>not_on_this_venue</c>).</exception>
    public Task<CandleList> AccountCandlesAsync(
        Guid accountId, string symbol, string timeframe, DateTimeOffset @from, DateTimeOffset to, int? limit = null,
        CancellationToken cancellationToken = default) =>
        _client.GetAsync<CandleList>("/accounts/" + Wire.Id(accountId) + "/md/candles",
            new Query().Add("symbol", symbol).Add("tf", Frame(timeframe)).Add("from", @from).Add("to", to).Add("limit", limit),
            cancellationToken);

    /// <summary>
    /// One read of tick history: the first <paramref name="limit"/> ticks at or after <paramref name="from"/>,
    /// oldest first, at most 1,000 a call. <c>more</c> is true when the window holds more;
    /// <see cref="AllTicksAsync"/> follows it.
    /// </summary>
    /// <param name="broker">The broker.</param>
    /// <param name="symbol">The broker's name for it.</param>
    /// <param name="from">From this instant.</param>
    /// <param name="to">Leaves out ticks after it.</param>
    /// <param name="limit">At most this many ticks.</param>
    /// <param name="serverId">Which server.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ticks.</returns>
    public Task<TickList> TicksAsync(
        Guid broker, string symbol, DateTimeOffset @from, DateTimeOffset? to = null, int? limit = null, Guid? serverId = null,
        CancellationToken cancellationToken = default) =>
        _client.GetAsync<TickList>("/md/ticks",
            new Query().Add("broker", broker).Add("symbol", symbol).Add("from", @from).Add("to", to).Add("limit", limit).Add("server_id", serverId),
            cancellationToken);

    /// <summary>
    /// Every tick of a window, in order, each once, a call at a time. Each next call starts at the last
    /// tick's time, and several ticks can share one millisecond, so the ticks already held at that time
    /// are dropped exactly once each: dropping by time alone would lose ticks, and dropping none would
    /// double them.
    /// </summary>
    /// <param name="broker">The broker.</param>
    /// <param name="symbol">The broker's name for it.</param>
    /// <param name="from">From this instant.</param>
    /// <param name="to">Leaves out ticks after it.</param>
    /// <param name="pageSize">How many a call asks for.</param>
    /// <param name="serverId">Which server.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every tick of the window.</returns>
    public async IAsyncEnumerable<Tick> AllTicksAsync(
        Guid broker, string symbol, DateTimeOffset @from, DateTimeOffset? to = null, int? pageSize = null, Guid? serverId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var start = @from;
        var held = new List<(DateTimeOffset, string, string, string?, string?)>();
        while (true)
        {
            var answer = await TicksAsync(broker, symbol, start, to, pageSize, serverId, cancellationToken).ConfigureAwait(false);
            var items = answer.Items?.ToList() ?? [];
            var fresh = new List<Tick>();
            foreach (var tick in items)
            {
                var key = (tick.Ts, tick.Bid, tick.Ask, tick.Last, tick.Volume);
                if (tick.Ts == start && held.Remove(key))
                {
                    continue;
                }
                fresh.Add(tick);
            }
            foreach (var tick in fresh)
            {
                yield return tick;
            }
            if (!answer.More || fresh.Count == 0)
            {
                // Nothing new with more promised is one millisecond holding more ticks than a call
                // returns; asking again would return the same ones for ever.
                yield break;
            }
            start = items[^1].Ts;
            held = [.. items.Where(t => t.Ts == start).Select(t => (t.Ts, t.Bid, t.Ask, t.Last, t.Volume))];
        }
    }

    private static string Frame(string timeframe, [CallerArgumentExpression(nameof(timeframe))] string parameter = "") =>
        Timeframes.Contains(timeframe, StringComparer.Ordinal)
            ? timeframe
            : throw new ArgumentException($"'{timeframe}' is not one of {string.Join(", ", Timeframes)}", parameter);
}

/// <summary>
/// One message's data, read by the contract's rules: a required field that is missing is a
/// <see cref="FormatException"/> naming it; an optional one that is absent or null is null. Values may
/// arrive as text (the stream writes every value of a flat event as text) or as JSON values.
/// </summary>
internal readonly struct EventData(string kind, IReadOnlyDictionary<string, JsonElement> data)
{
    public string Kind => kind;

    public EventData(string kind, JsonElement element)
        : this(kind, element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
            : throw new FormatException($"{kind} is not an object"))
    {
    }

    public JsonElement Raw(string key, bool required)
    {
        var value = data.TryGetValue(key, out var found) ? found : default;
        if (required && value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new FormatException($"{kind} carries no `{key}`");
        }
        return value;
    }

    private static bool Absent(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    private static string AsText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    public string Text(string key) => AsText(Raw(key, true));

    public string? TextOrNull(string key) => Raw(key, false) is var v && Absent(v) ? null : AsText(v);

    /// <summary>Text, where the platform sends an empty string for none (a flat event's field).</summary>
    public string? BlankNull(string key) => TextOrNull(key) is { Length: > 0 } text ? text : null;

    public Guid Uuid(string key) => Guid.Parse(Text(key), CultureInfo.InvariantCulture);

    public Guid? UuidOrNull(string key) => TextOrNull(key) is { } text ? Guid.Parse(text, CultureInfo.InvariantCulture) : null;

    public long Integer(string key) => long.Parse(Text(key), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    public long? IntegerOrNull(string key) => TextOrNull(key) is { } text ? long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : null;

    public decimal Decimal(string key) => Exact(Raw(key, true));

    public decimal? DecimalOrNull(string key) => Raw(key, false) is var v && Absent(v) ? null : Exact(v);

    public DateTimeOffset Instant(string key) => InstantOf(Text(key));

    public DateTimeOffset? InstantOrNull(string key) => TextOrNull(key) is { } text ? InstantOf(text) : null;

    public double? NumberOrNull(string key) => TextOrNull(key) is { } text ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) : null;

    /// <summary>A nested part: the stream carries it as JSON text, a webhook body as JSON itself.</summary>
    public JsonElement Json(string key)
    {
        var value = Raw(key, false);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
        return value;
    }

    /// <summary>A nested object the contract requires, read by the same rules.</summary>
    public EventData Part(string key)
    {
        var value = Json(key);
        return value.ValueKind == JsonValueKind.Object
            ? new EventData($"{kind} `{key}`", value)
            : throw new FormatException($"{kind} `{key}` is not an object");
    }

    /// <summary>A list of nested objects; absent or null is an empty list.</summary>
    public IEnumerable<EventData> Parts(string key)
    {
        var value = Json(key);
        var name = $"{kind} `{key}`";
        return value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(item => new EventData(name, item))] : [];
    }

    /// <summary>An exact decimal from the API's text, or from a JSON number written as the API wrote it.</summary>
    public static decimal Exact(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? decimal.Parse(AsText(value), NumberStyles.Float, CultureInfo.InvariantCulture)
            : throw new FormatException($"'{value.GetRawText()}' is not a decimal");

    private static DateTimeOffset InstantOf(string text)
    {
        var moment = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return moment;
    }
}
