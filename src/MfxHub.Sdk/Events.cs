using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace MfxHub;

/// <summary>
/// A stream message read as the typed event it is: the order, position, terminal, candle, Risk
/// Management and copy events. <see cref="Of"/> reads one message, or answers null for any other type.
/// </summary>
/// <remarks>
/// <code>
/// await foreach (var e in stream)
/// {
///     if (e is StreamMessage m &amp;&amp; MfxEvent.Of(m) is OrderClosed { Reason: "filled" } closed)
///         Console.WriteLine($"order {closed.Ticket} became position {closed.PositionTicket}");
/// }
/// </code>
/// Ids are <see cref="Guid"/>s, tickets and magic numbers <see cref="long"/>s, prices, volumes and money
/// <see cref="decimal"/>s, and times <see cref="DateTimeOffset"/>s. The platform's own words (a side, an
/// order type, a reason, a state, a timeframe) stay text: they are not a closed list, and a word added
/// later reads as it was written instead of failing the read. The message keeps its data whatever this
/// returns, so nothing is lost by a type this version does not know.
/// </remarks>
/// <param name="Kind">The event's type, such as <c>order.placed</c>.</param>
public abstract record MfxEvent(string Kind)
{
    private static readonly FrozenDictionary<string, Func<EventData, MfxEvent>> Readers = new Dictionary<string, Func<EventData, MfxEvent>>
    {
        [OrderPlaced.Type] = d => new OrderPlaced(d),
        [OrderModified.Type] = d => new OrderModified(d),
        [OrderClosed.Type] = d => new OrderClosed(d),
        [PositionOpened.Type] = d => new PositionOpened(d),
        [PositionModified.Type] = d => new PositionModified(d),
        [PositionClosed.Type] = d => new PositionClosed(d),
        [TerminalStateChanged.Type] = d => new TerminalStateChanged(d),
        [CandleClosed.Type] = d => new CandleClosed(d),
        [RiskRuleEvent.Warning] = d => new RiskRuleEvent(d),
        [RiskRuleEvent.Breached] = d => new RiskRuleEvent(d),
        [RiskRuleEvent.Cleared] = d => new RiskRuleEvent(d),
        [RiskCoverageChanged.Type] = d => new RiskCoverageChanged(d),
        [RiskProgrammeResult.Type] = d => new RiskProgrammeResult(d),
        [RiskPeriodClosed.Type] = d => new RiskPeriodClosed(d),
        [CopyCopied.Type] = d => new CopyCopied(d),
        [CopyRefused.Type] = d => new CopyRefused(d),
        [CopyStopoutEvent.Type] = d => new CopyStopoutEvent(d),
        [CopyDrift.Type] = d => new CopyDrift(d),
        [CopyStateChanged.Type] = d => new CopyStateChanged(d),
        [CopyClosedEvent.Type] = d => new CopyClosedEvent(d),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The event types <see cref="Of"/> reads. Any other type is null, never an error.</summary>
    public static IReadOnlySet<string> Kinds { get; } = Readers.Keys.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// A stream message as the typed event it is, or null for a message of any other type. A candle is
    /// read by its channel: <c>candles:</c> messages carry the bar and no event type.
    /// </summary>
    /// <param name="message">A message from the stream.</param>
    /// <returns>The event, or null.</returns>
    /// <exception cref="FormatException">A message of one of these types lacks a field the contract requires.</exception>
    public static MfxEvent? Of(StreamMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var kind = message.Type;
        if (kind is null)
        {
            if (!message.Channel.StartsWith("candles:", StringComparison.Ordinal))
            {
                return null;
            }
            kind = CandleClosed.Type;
        }
        return Readers.TryGetValue(kind, out var read) ? read(new EventData(kind, message.Data)) : null;
    }
}

/// <summary>A pending order (a buy or sell limit, stop or stop-limit order), as its events carry it.</summary>
public abstract record OrderEvent : MfxEvent
{
    private protected OrderEvent(string kind, EventData d)
        : base(kind)
    {
        SchemaVersion = d.Text("schema_version");
        TerminalId = d.Uuid("terminal_id");
        EventId = d.Text("event_id");
        Ticket = d.Integer("ticket");
        Symbol = d.Text("symbol");
        Side = d.Text("side");
        OrderType = d.Text("order_type");
        Volume = d.Decimal("volume");
        Price = d.Decimal("price");
        StopLimitPrice = d.DecimalOrNull("stop_limit_price");
        Sl = d.DecimalOrNull("sl");
        Tp = d.DecimalOrNull("tp");
        PlacedTime = d.Instant("placed_time");
        ExpiresAt = d.InstantOrNull("expires_at");
        Magic = d.Integer("magic");
        Comment = d.Text("comment");
        Ts = d.Instant("ts");
        IdempotencyKey = d.TextOrNull("idempotency_key");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The terminal.</summary>
    public Guid TerminalId { get; init; }

    /// <summary>The same for a replay of the same change: drop one already seen.</summary>
    public string EventId { get; init; }

    /// <summary>The order's ticket.</summary>
    public long Ticket { get; init; }

    /// <summary>The broker's symbol.</summary>
    public string Symbol { get; init; }

    /// <summary><c>buy</c> or <c>sell</c>.</summary>
    public string Side { get; init; }

    /// <summary><c>limit</c>, <c>stop</c> or <c>stop_limit</c>.</summary>
    public string OrderType { get; init; }

    /// <summary>Lots still to fill.</summary>
    public decimal Volume { get; init; }

    /// <summary>The price; on a stop-limit order, the stop price.</summary>
    public decimal Price { get; init; }

    /// <summary>A stop-limit order's limit, placed when the market reaches <see cref="Price"/>.</summary>
    public decimal? StopLimitPrice { get; init; }

    /// <summary>The stop loss.</summary>
    public decimal? Sl { get; init; }

    /// <summary>The take profit.</summary>
    public decimal? Tp { get; init; }

    /// <summary>When it was placed.</summary>
    public DateTimeOffset PlacedTime { get; init; }

    /// <summary>When it expires; null when good till cancelled.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The magic number.</summary>
    public long Magic { get; init; }

    /// <summary>The comment.</summary>
    public string Comment { get; init; }

    /// <summary>When the change was seen.</summary>
    public DateTimeOffset Ts { get; init; }

    /// <summary>The Idempotency-Key of the platform command that made this change; null for a change the platform did not make.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary><c>order.placed</c>: a pending order appeared on the account, placed by anyone.</summary>
[ContractSchema("OrderPlaced")]
public sealed record OrderPlaced : OrderEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "order.placed";

    internal OrderPlaced(EventData d)
        : base(Type, d)
    {
    }
}

/// <summary><c>order.modified</c>: a pending order's price, stops, expiry or volume changed.</summary>
[ContractSchema("OrderModified")]
public sealed record OrderModified : OrderEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "order.modified";

    internal OrderModified(EventData d)
        : base(Type, d)
    {
    }
}

/// <summary>
/// <c>order.closed</c>: a pending order left the account. On <c>filled</c>, <see cref="PositionTicket"/> is
/// the position it became.
/// </summary>
[ContractSchema("OrderClosed")]
public sealed record OrderClosed : OrderEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "order.closed";

    internal OrderClosed(EventData d)
        : base(Type, d)
    {
        Reason = d.Text("reason");
        OrderStateRaw = d.TextOrNull("order_state_raw");
        PositionTicket = d.IntegerOrNull("position_ticket");
    }

    /// <summary><c>filled</c>, <c>cancelled</c>, <c>expired</c>, <c>rejected</c> or <c>unknown</c> today.</summary>
    public string Reason { get; init; }

    /// <summary>MetaTrader's own word for the order's last state, when the reason was read from it.</summary>
    public string? OrderStateRaw { get; init; }

    /// <summary>The position it became, on <c>filled</c>.</summary>
    public long? PositionTicket { get; init; }
}

/// <summary>A position, as its events carry it.</summary>
public abstract record PositionEvent : MfxEvent
{
    private protected PositionEvent(string kind, EventData d)
        : base(kind)
    {
        SchemaVersion = d.Text("schema_version");
        TerminalId = d.Uuid("terminal_id");
        EventId = d.Text("event_id");
        Ticket = d.Integer("ticket");
        Symbol = d.Text("symbol");
        Side = d.Text("side");
        Volume = d.Decimal("volume");
        OpenPrice = d.Decimal("open_price");
        PriceCurrent = d.DecimalOrNull("price_current");
        Sl = d.DecimalOrNull("sl");
        Tp = d.DecimalOrNull("tp");
        Profit = d.DecimalOrNull("profit");
        Swap = d.DecimalOrNull("swap");
        OpenTime = d.Instant("open_time");
        Magic = d.Integer("magic");
        Comment = d.Text("comment");
        Ts = d.Instant("ts");
        IdempotencyKey = d.TextOrNull("idempotency_key");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The terminal.</summary>
    public Guid TerminalId { get; init; }

    /// <summary>The same for a replay of the same change.</summary>
    public string EventId { get; init; }

    /// <summary>The position's ticket.</summary>
    public long Ticket { get; init; }

    /// <summary>The broker's symbol.</summary>
    public string Symbol { get; init; }

    /// <summary><c>buy</c> or <c>sell</c>.</summary>
    public string Side { get; init; }

    /// <summary>The volume, in lots.</summary>
    public decimal Volume { get; init; }

    /// <summary>The open price.</summary>
    public decimal OpenPrice { get; init; }

    /// <summary>The current price.</summary>
    public decimal? PriceCurrent { get; init; }

    /// <summary>The stop loss.</summary>
    public decimal? Sl { get; init; }

    /// <summary>The take profit.</summary>
    public decimal? Tp { get; init; }

    /// <summary>The floating profit.</summary>
    public decimal? Profit { get; init; }

    /// <summary>The swap.</summary>
    public decimal? Swap { get; init; }

    /// <summary>When it opened.</summary>
    public DateTimeOffset OpenTime { get; init; }

    /// <summary>The magic number.</summary>
    public long Magic { get; init; }

    /// <summary>The comment.</summary>
    public string Comment { get; init; }

    /// <summary>When the change was seen.</summary>
    public DateTimeOffset Ts { get; init; }

    /// <summary>The Idempotency-Key of the platform command that made this change, or null.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary><c>position.opened</c>: a position appeared on the account.</summary>
[ContractSchema("PositionOpened")]
public sealed record PositionOpened : PositionEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "position.opened";

    internal PositionOpened(EventData d)
        : base(Type, d) => OrderTicket = d.IntegerOrNull("order_ticket");

    /// <summary>The pending order it filled from; null for a market order.</summary>
    public long? OrderTicket { get; init; }
}

/// <summary><c>position.modified</c>: a position's stops or volume changed.</summary>
[ContractSchema("PositionModified")]
public sealed record PositionModified : PositionEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "position.modified";

    internal PositionModified(EventData d)
        : base(Type, d)
    {
    }
}

/// <summary><c>position.closed</c>: a position left the account.</summary>
[ContractSchema("PositionClosed")]
public sealed record PositionClosed : PositionEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "position.closed";

    internal PositionClosed(EventData d)
        : base(Type, d)
    {
        ClosePrice = d.DecimalOrNull("close_price");
        CloseReason = d.Text("close_reason");
        DealReasonRaw = d.TextOrNull("deal_reason_raw");
    }

    /// <summary>The close price.</summary>
    public decimal? ClosePrice { get; init; }

    /// <summary><c>client</c>, <c>sl</c>, <c>tp</c>, <c>stop_out</c> and the rest, as text.</summary>
    public string CloseReason { get; init; }

    /// <summary>MetaTrader's own word for the closing deal's reason.</summary>
    public string? DealReasonRaw { get; init; }
}

/// <summary><c>terminal.state_changed</c>: a terminal moved from one state to another.</summary>
[ContractSchema("TerminalStateChanged")]
public sealed record TerminalStateChanged : MfxEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "terminal.state_changed";

    internal TerminalStateChanged(EventData d)
        : base(Type)
    {
        SchemaVersion = d.Text("schema_version");
        TerminalId = d.Uuid("terminal_id");
        FromState = d.Text("from");
        ToState = d.Text("to");
        Reason = d.Text("reason");
        TransitionId = d.Uuid("transition_id");
        Ts = d.Instant("ts");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The terminal.</summary>
    public Guid TerminalId { get; init; }

    /// <summary>The contract's <c>from</c>: ACTIVE, OFFLINE, RETIRED and the rest, as text.</summary>
    public string FromState { get; init; }

    /// <summary>The contract's <c>to</c>.</summary>
    public string ToState { get; init; }

    /// <summary>Why.</summary>
    public string Reason { get; init; }

    /// <summary>The transition.</summary>
    public Guid TransitionId { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset Ts { get; init; }
}

/// <summary>
/// <c>md.candle_closed</c> on <c>candles:{server_id}:{symbol}:{tf}</c>: a closed bar, sent once and never
/// replayed. <see cref="PrevTs"/> is the bar before it: if it is not the last one held, one was missed.
/// </summary>
[ContractSchema("MdCandleClosed")]
public sealed record CandleClosed : MfxEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "md.candle_closed";

    internal CandleClosed(EventData d)
        : base(Type)
    {
        SchemaVersion = d.Text("schema_version");
        ServerId = d.Uuid("server_id");
        Symbol = d.Text("symbol");
        Timeframe = d.Text("timeframe");
        Ts = d.Instant("ts");
        O = d.Decimal("o");
        H = d.Decimal("h");
        L = d.Decimal("l");
        C = d.Decimal("c");
        V = d.Integer("v");
        PrevTs = d.Instant("prev_ts");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The broker server.</summary>
    public Guid ServerId { get; init; }

    /// <summary>The broker's symbol.</summary>
    public string Symbol { get; init; }

    /// <summary>The bar's timeframe.</summary>
    public string Timeframe { get; init; }

    /// <summary>The bar's open time.</summary>
    public DateTimeOffset Ts { get; init; }

    /// <summary>The open.</summary>
    public decimal O { get; init; }

    /// <summary>The high.</summary>
    public decimal H { get; init; }

    /// <summary>The low.</summary>
    public decimal L { get; init; }

    /// <summary>The close.</summary>
    public decimal C { get; init; }

    /// <summary>The tick volume.</summary>
    public long V { get; init; }

    /// <summary>The bar before it.</summary>
    public DateTimeOffset PrevTs { get; init; }
}

/// <summary>
/// <c>risk.rule_warning</c>, <c>risk.rule_breached</c> or <c>risk.rule_cleared</c>: a rule of a policy
/// (<see cref="PolicyId"/>) or of a challenge (<see cref="ChallengeId"/>) reached its warning or action
/// level, or cleared. Figures are in the account's currency.
/// </summary>
[ContractSchema("RiskRuleEvent")]
public sealed record RiskRuleEvent : MfxEvent
{
    /// <summary>A rule reached its warning level.</summary>
    public const string Warning = "risk.rule_warning";

    /// <summary>A rule reached its action level.</summary>
    public const string Breached = "risk.rule_breached";

    /// <summary>A rule cleared.</summary>
    public const string Cleared = "risk.rule_cleared";

    internal RiskRuleEvent(EventData d)
        : base(d.Kind)
    {
        SchemaVersion = d.Text("schema_version");
        AccountId = d.Uuid("account_id");
        Rule = d.Text("rule");
        PolicyId = d.BlankNull("policy_id") is { } policy ? Guid.Parse(policy, CultureInfo.InvariantCulture) : null;
        ChallengeId = d.BlankNull("challenge_id") is { } challenge ? Guid.Parse(challenge, CultureInfo.InvariantCulture) : null;
        Version = d.BlankNull("version") is { } version ? int.Parse(version, CultureInfo.InvariantCulture) : null;
        Used = d.BlankNull("used") is { } used ? ExactDecimalConverter.Parse(used) : null;
        Limit = d.BlankNull("limit") is { } limit ? ExactDecimalConverter.Parse(limit) : null;
        Level = d.Text("level");
        var actions = d.Json("actions");
        Actions = actions.ValueKind == JsonValueKind.Array
            ? [.. actions.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText())]
            : [];
        ClearedBy = d.BlankNull("cleared_by");
        At = d.Instant("at");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The account.</summary>
    public Guid AccountId { get; init; }

    /// <summary>The rule.</summary>
    public string Rule { get; init; }

    /// <summary>The policy whose rule it is; null for a challenge's.</summary>
    public Guid? PolicyId { get; init; }

    /// <summary>The challenge whose rule it is; null for a policy's.</summary>
    public Guid? ChallengeId { get; init; }

    /// <summary>The version of the policy or challenge.</summary>
    public int? Version { get; init; }

    /// <summary>What the rule's figure has used.</summary>
    public decimal? Used { get; init; }

    /// <summary>The rule's limit.</summary>
    public decimal? Limit { get; init; }

    /// <summary><c>warn</c>, <c>act</c> or <c>cleared</c>.</summary>
    public string Level { get; init; }

    /// <summary>What the rule authorised: <c>notify</c>, <c>block_entries</c>, <c>cancel_pending</c>, <c>close_positions</c>.</summary>
    public IReadOnlyList<string> Actions { get; init; }

    /// <summary><c>reset</c>, <c>override</c> or <c>below_warn</c> on a clear; null otherwise.</summary>
    public string? ClearedBy { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary><c>risk.coverage_changed</c>: one kind of an account's coverage changed state.</summary>
[ContractSchema("RiskCoverageChanged")]
public sealed record RiskCoverageChanged : MfxEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "risk.coverage_changed";

    internal RiskCoverageChanged(EventData d)
        : base(Type)
    {
        SchemaVersion = d.Text("schema_version");
        AccountId = d.Uuid("account_id");
        Coverage = d.Text("kind");
        FromState = d.Text("from");
        ToState = d.Text("to");
        Reason = d.BlankNull("reason");
        At = d.Instant("at");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The account.</summary>
    public Guid AccountId { get; init; }

    /// <summary><c>monitoring</c>, <c>pre_trade</c>, <c>broker_held</c> or <c>managed_actions</c> (the contract's <c>kind</c>).</summary>
    public string Coverage { get; init; }

    /// <summary>The contract's <c>from</c>: <c>healthy</c>, <c>degraded</c>, <c>disconnected</c>, <c>unknown</c>.</summary>
    public string FromState { get; init; }

    /// <summary>The contract's <c>to</c>.</summary>
    public string ToState { get; init; }

    /// <summary>Why, when the platform says.</summary>
    public string? Reason { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary><c>risk.programme_result</c>: a challenge account passed, failed or ended. Once only.</summary>
[ContractSchema("RiskProgrammeResult")]
public sealed record RiskProgrammeResult : MfxEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "risk.programme_result";

    internal RiskProgrammeResult(EventData d)
        : base(Type)
    {
        SchemaVersion = d.Text("schema_version");
        ChallengeId = d.Uuid("challenge_id");
        Version = (int)d.Integer("version");
        AccountId = d.Uuid("account_id");
        Result = d.Text("result");
        Rule = d.BlankNull("rule");
        At = d.Instant("at");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The challenge.</summary>
    public Guid ChallengeId { get; init; }

    /// <summary>The challenge's version.</summary>
    public int Version { get; init; }

    /// <summary>The account.</summary>
    public Guid AccountId { get; init; }

    /// <summary><c>passed</c>, <c>failed</c> or <c>ended</c>.</summary>
    public string Result { get; init; }

    /// <summary>The rule it failed on; null on a pass or an end.</summary>
    public string? Rule { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// <c>risk.period_closed</c>: one period of a challenge account closed, with its statistics (the
/// contract's <c>RiskPeriod</c>, as a map: figures as text).
/// </summary>
[ContractSchema("RiskPeriodClosed")]
public sealed record RiskPeriodClosed : MfxEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "risk.period_closed";

    internal RiskPeriodClosed(EventData d)
        : base(Type)
    {
        SchemaVersion = d.Text("schema_version");
        ChallengeId = d.Uuid("challenge_id");
        AccountId = d.Uuid("account_id");
        var period = d.Json("period");
        Period = period.ValueKind == JsonValueKind.Object
            ? period.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : throw new FormatException("risk.period_closed carries no `period`");
        At = d.Instant("at");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The challenge.</summary>
    public Guid ChallengeId { get; init; }

    /// <summary>The account.</summary>
    public Guid AccountId { get; init; }

    /// <summary>The period and its statistics.</summary>
    public IReadOnlyDictionary<string, JsonElement> Period { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>The leader's deal a copy event is about.</summary>
/// <param name="Action"><c>open</c>, <c>modify</c>, <c>close</c>, <c>close_part</c>, <c>pending_place</c>, <c>pending_modify</c> or <c>pending_cancel</c>.</param>
/// <param name="Side"><c>buy</c> or <c>sell</c>.</param>
/// <param name="Units">The leader's volume in base units.</param>
/// <param name="Price">The price.</param>
/// <param name="InstrumentKey">The venue-neutral instrument key (<c>fx:EURUSD</c>), when the leader's symbol has one.</param>
[ContractSchema("CopyLeaderDeal")]
public sealed record CopyDeal(string Action, string Side, decimal Units, decimal? Price, string? InstrumentKey)
{
    internal static CopyDeal Of(EventData d) =>
        new(d.Text("action"), d.Text("side"), d.Decimal("units"), d.DecimalOrNull("price"), d.TextOrNull("instrument_key"));
}

/// <summary>What a copy became on the follower account.</summary>
/// <param name="Symbol">The follower's symbol.</param>
/// <param name="Side">The side.</param>
/// <param name="Volume">The volume, in the follower venue's unit.</param>
/// <param name="Price">The price.</param>
public sealed record CopyAs(string Symbol, string Side, decimal Volume, decimal? Price);

/// <summary>Copied volume a stop-out or a requested close closed.</summary>
/// <param name="Symbol">The symbol.</param>
/// <param name="Volume">The volume closed.</param>
/// <param name="CommandId">The command that closed it.</param>
public sealed record CopyClosed(string Symbol, decimal Volume, Guid? CommandId)
{
    internal static IReadOnlyList<CopyClosed> Of(EventData d, string key) =>
        [.. d.Parts(key).Select(item => new CopyClosed(item.Text("symbol"), item.Decimal("volume"), item.UuidOrNull("command_id")))];
}

/// <summary>A copied position a requested close did not close.</summary>
/// <param name="Symbol">The symbol.</param>
/// <param name="Volume">The volume left.</param>
/// <param name="Why"><c>netting</c>, <c>not_copied</c>, <c>copy_mismatch</c> or <c>gave_up</c>; null when not known.</param>
public sealed record CopyNotClosed(string Symbol, decimal? Volume, string? Why);

/// <summary>What every event about one follower carries.</summary>
public abstract record CopyFollowerEvent : MfxEvent
{
    private protected CopyFollowerEvent(string kind, EventData d)
        : base(kind)
    {
        SchemaVersion = d.Text("schema_version");
        FollowerId = d.Uuid("follower_id");
        LeaderId = d.Uuid("leader_id");
        AccountId = d.Uuid("account_id");
        Venue = d.Text("venue");
        At = d.Instant("at");
    }

    /// <summary>The event's schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>The follower.</summary>
    public Guid FollowerId { get; init; }

    /// <summary>The leader.</summary>
    public Guid LeaderId { get; init; }

    /// <summary>The follower account.</summary>
    public Guid AccountId { get; init; }

    /// <summary><c>mt5</c> or <c>ctrader</c> today.</summary>
    public string Venue { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary><c>copy.copied</c>: one follower's copy filled.</summary>
[ContractSchema("CopyCopied")]
public sealed record CopyCopied : CopyFollowerEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "copy.copied";

    internal CopyCopied(EventData d)
        : base(Type, d)
    {
        SignalId = d.Uuid("signal_id");
        CommandId = d.Uuid("command_id");
        Deal = CopyDeal.Of(d.Part("deal"));
        var became = d.Part("as");
        As = new CopyAs(became.Text("symbol"), became.Text("side"), became.Decimal("volume"), became.DecimalOrNull("price"));
        LatencyMs = d.NumberOrNull("latency_ms");
        Slippage = d.DecimalOrNull("slippage");
        var stages = d.Json("stages");
        Stages = stages.ValueKind == JsonValueKind.Object
            ? stages.EnumerateObject().Where(p => p.Value.ValueKind is not JsonValueKind.Null)
                .ToDictionary(p => p.Name, p => double.Parse(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText(),
                    NumberStyles.Float, CultureInfo.InvariantCulture), StringComparer.Ordinal)
            : new Dictionary<string, double>(StringComparer.Ordinal);
    }

    /// <summary>MFX Hub's id for the signal; the copy log names it.</summary>
    public Guid SignalId { get; init; }

    /// <summary>The command the copy became.</summary>
    public Guid CommandId { get; init; }

    /// <summary>The leader's deal.</summary>
    public CopyDeal Deal { get; init; }

    /// <summary>What the copy became (the contract's <c>as</c>).</summary>
    public CopyAs As { get; init; }

    /// <summary>MFX Hub's part, in milliseconds: a measurement, not money.</summary>
    public double? LatencyMs { get; init; }

    /// <summary>The slippage.</summary>
    public decimal? Slippage { get; init; }

    /// <summary>Milliseconds by stage (<c>detect_ms</c>, <c>sign_ms</c> and the rest): the ones measured.</summary>
    public IReadOnlyDictionary<string, double> Stages { get; init; }
}

/// <summary><c>copy.refused</c>: one follower's copy did not fill: refused, skipped, or a stop-out's close.</summary>
[ContractSchema("CopyRefused")]
public sealed record CopyRefused : CopyFollowerEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "copy.refused";

    internal CopyRefused(EventData d)
        : base(Type, d)
    {
        SignalId = d.Uuid("signal_id");
        Deal = CopyDeal.Of(d.Part("deal"));
        Outcome = d.Text("outcome");
        Reason = d.Text("reason");
        VenueCode = d.TextOrNull("venue_code");
        Why = d.TextOrNull("why");
        var risk = d.Json("risk");
        if (risk.ValueKind == JsonValueKind.Object)
        {
            var r = new EventData("copy.refused `risk`", risk);
            Risk = new RiskRefusal(r.Text("reason"), r.TextOrNull("rule"), r.TextOrNull("policy_id"), (int?)r.IntegerOrNull("version"),
                r.TextOrNull("challenge_id"), (int?)r.IntegerOrNull("challenge_version"), r.TextOrNull("used"), r.TextOrNull("limit"), r.TextOrNull("would_be"));
        }
    }

    /// <summary>MFX Hub's id for the signal.</summary>
    public Guid SignalId { get; init; }

    /// <summary>The leader's deal.</summary>
    public CopyDeal Deal { get; init; }

    /// <summary><c>refused</c>, <c>skipped</c> or <c>stopout</c>.</summary>
    public string Outcome { get; init; }

    /// <summary>One of <see cref="Copy.RefusalReasons"/>.</summary>
    public string Reason { get; init; }

    /// <summary>The venue's own code beside <c>broker</c>.</summary>
    public string? VenueCode { get; init; }

    /// <summary>The reason in words, for a person.</summary>
    public string? Why { get; init; }

    /// <summary>When Risk Management refused the copy (reason <c>risk_rule</c>): the rule and the figures as text; null otherwise.</summary>
    public RiskRefusal? Risk { get; init; }
}

/// <summary><c>copy.closed</c>: a requested close of copied positions stopped being pending. Sent once.</summary>
[ContractSchema("CopyClosedEvent")]
public sealed record CopyClosedEvent : CopyFollowerEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "copy.closed";

    internal CopyClosedEvent(EventData d)
        : base(Type, d)
    {
        CloseId = d.Uuid("close_id");
        Cause = d.Text("cause");
        State = d.Text("state");
        Closed = CopyClosed.Of(d, "closed");
        NotClosed = [.. d.Parts("not_closed").Select(item => new CopyNotClosed(item.Text("symbol"), item.DecimalOrNull("volume"), item.TextOrNull("why")))];
    }

    /// <summary>The requested close.</summary>
    public Guid CloseId { get; init; }

    /// <summary><c>unfollowed</c>, <c>leader_retired</c> or <c>group_close</c>.</summary>
    public string Cause { get; init; }

    /// <summary><c>done</c>, <c>incomplete</c> or <c>superseded</c>.</summary>
    public string State { get; init; }

    /// <summary>What it closed.</summary>
    public IReadOnlyList<CopyClosed> Closed { get; init; }

    /// <summary>What it did not close.</summary>
    public IReadOnlyList<CopyNotClosed> NotClosed { get; init; }
}

/// <summary><c>copy.stopout</c>: a follower reached a copy limit and is stopped out.</summary>
[ContractSchema("CopyStopoutEvent")]
public sealed record CopyStopoutEvent : CopyFollowerEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "copy.stopout";

    internal CopyStopoutEvent(EventData d)
        : base(Type, d)
    {
        StopoutId = d.Uuid("stopout_id");
        Limit = d.Text("limit");
        Used = d.Decimal("used");
        Closed = CopyClosed.Of(d, "closed");
    }

    /// <summary>The stop-out.</summary>
    public Guid StopoutId { get; init; }

    /// <summary><c>daily</c>, <c>monthly</c> or <c>lifetime</c>.</summary>
    public string Limit { get; init; }

    /// <summary>What was used, in the follower account's currency.</summary>
    public decimal Used { get; init; }

    /// <summary>The copied volume closed, when the follower closes on a stop-out.</summary>
    public IReadOnlyList<CopyClosed> Closed { get; init; }
}

/// <summary>
/// <c>copy.drift</c>: the follower's copied positions differ from what the leader's positions and the
/// mandate say. MFX Hub trades nothing to close the gap.
/// </summary>
[ContractSchema("CopyDrift")]
public sealed record CopyDrift : CopyFollowerEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "copy.drift";

    internal CopyDrift(EventData d)
        : base(Type, d) => DriftPct = d.Decimal("drift_pct");

    /// <summary>How far the copied exposure is from the expected, as a percentage of the expected.</summary>
    public decimal DriftPct { get; init; }
}

/// <summary><c>copy.state_changed</c>: a follower's state changed.</summary>
[ContractSchema("CopyStateChanged")]
public sealed record CopyStateChanged : CopyFollowerEvent
{
    /// <summary>The event type.</summary>
    public const string Type = "copy.state_changed";

    internal CopyStateChanged(EventData d)
        : base(Type, d)
    {
        FromState = d.Text("from");
        ToState = d.Text("to");
        Why = d.Text("why");
    }

    /// <summary>The contract's <c>from</c>: <c>copying</c>, <c>paused</c>, <c>stopped_out</c> or <c>unfollowed</c>.</summary>
    public string FromState { get; init; }

    /// <summary>The contract's <c>to</c>.</summary>
    public string ToState { get; init; }

    /// <summary><c>paused</c>, <c>resumed</c>, <c>stopout</c>, <c>unfollowed</c>, <c>leader_retired</c> and others.</summary>
    public string Why { get; init; }
}
