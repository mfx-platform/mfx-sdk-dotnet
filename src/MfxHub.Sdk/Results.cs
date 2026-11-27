using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MfxHub.Models;

namespace MfxHub;

/// <summary>Which way an order goes.</summary>
public enum Side
{
    /// <summary>Buy.</summary>
    [JsonStringEnumMemberName("buy")]
    Buy,

    /// <summary>Sell.</summary>
    [JsonStringEnumMemberName("sell")]
    Sell,
}

/// <summary>The kind of an order: at the market, or pending at a price.</summary>
public enum OrderType
{
    /// <summary>At the market, now.</summary>
    [JsonStringEnumMemberName("market")]
    Market,

    /// <summary>A pending limit order at its price.</summary>
    [JsonStringEnumMemberName("limit")]
    Limit,

    /// <summary>A pending stop order at its price.</summary>
    [JsonStringEnumMemberName("stop")]
    Stop,

    /// <summary>A stop that places a limit order (its <c>stopLimitPrice</c>) when its price is reached.</summary>
    [JsonStringEnumMemberName("stop_limit")]
    StopLimit,
}

/// <summary>
/// Why a command ended as it did: the platform's reasons, and the three the SDK concludes itself
/// (<see cref="TransportError"/>, <see cref="Timeout"/>, <see cref="Refused"/>). A reason the platform
/// adds later reads as null here and arrives whole in <see cref="ExecutionResult.ReasonText"/>.
/// </summary>
public enum Reason
{
    /// <summary>It succeeded.</summary>
    [JsonStringEnumMemberName("success")] Success,

    /// <summary>The broker requoted.</summary>
    [JsonStringEnumMemberName("requote")] Requote,

    /// <summary>The instrument is disabled.</summary>
    [JsonStringEnumMemberName("instrument_disabled")] InstrumentDisabled,

    /// <summary>No liquidity.</summary>
    [JsonStringEnumMemberName("no_liquidity")] NoLiquidity,

    /// <summary>The market is closed.</summary>
    [JsonStringEnumMemberName("market_closed")] MarketClosed,

    /// <summary>The stops are invalid.</summary>
    [JsonStringEnumMemberName("invalid_stops")] InvalidStops,

    /// <summary>The volume is invalid.</summary>
    [JsonStringEnumMemberName("invalid_volume")] InvalidVolume,

    /// <summary>Not enough money.</summary>
    [JsonStringEnumMemberName("insufficient_funds")] InsufficientFunds,

    /// <summary>Trading is disabled.</summary>
    [JsonStringEnumMemberName("trade_disabled")] TradeDisabled,

    /// <summary>Rejected for another reason; the broker's comment says which.</summary>
    [JsonStringEnumMemberName("rejected_other")] RejectedOther,

    /// <summary>Rate limited: retryable.</summary>
    [JsonStringEnumMemberName("rate_limited")] RateLimited,

    /// <summary>It expired before it ran.</summary>
    [JsonStringEnumMemberName("expired")] Expired,

    /// <summary>The outcome could not be read. Not a failure, and never to be sent again under a new key.</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,

    /// <summary>A maintenance window suspended order placement: nothing reached the broker; retryable once it ends.</summary>
    [JsonStringEnumMemberName("maintenance")] Maintenance,

    /// <summary>The venue did not answer, or its connection to the account is down: nothing was placed.</summary>
    [JsonStringEnumMemberName("venue_unavailable")] VenueUnavailable,

    /// <summary>The account's venue has no equivalent for what was asked. Not retryable as it stands.</summary>
    [JsonStringEnumMemberName("not_on_this_venue")] NotOnThisVenue,

    /// <summary>Concluded by the SDK: the request never got an answer.</summary>
    [JsonStringEnumMemberName("transport_error")] TransportError,

    /// <summary>Concluded by the SDK: no answer in time. Never retried: a timed-out order may have been placed.</summary>
    [JsonStringEnumMemberName("timeout")] Timeout,

    /// <summary>Concluded by the SDK: the platform refused the request itself (a 4xx that is not a rate limit).</summary>
    [JsonStringEnumMemberName("refused")] Refused,
}

/// <summary>The terminal relates one order, one deal and one position per fill; null where the terminal has none yet.</summary>
/// <param name="Order">The order ticket.</param>
/// <param name="Deal">The deal ticket.</param>
/// <param name="Position">The position ticket: what closing or modifying the position takes.</param>
[ContractSchema("CommandTickets")]
public sealed record Tickets(long? Order = null, long? Deal = null, long? Position = null);

/// <summary>
/// One deal a command made, as the venue reported it (cTrader): two for an emulated close-by, one for
/// an order filled at once. <see cref="VolumeUnits"/> is in units of the instrument, not lots.
/// </summary>
/// <param name="Deal">The deal number.</param>
/// <param name="Order">The order.</param>
/// <param name="Position">The position.</param>
/// <param name="VolumeUnits">The volume, in units.</param>
/// <param name="Price">The price.</param>
public sealed record VenueDeal(long Deal, long? Order, long? Position, decimal? VolumeUnits, decimal? Price);

/// <summary>
/// The whole answer to one execution call. Never thrown: always returned.
/// </summary>
/// <remarks>
/// Read <see cref="State"/> first: SUCCEEDED is done; FAILED, EXPIRED and REJECTED did not happen;
/// UNKNOWN may already be a position and must never be sent again under a new key.
/// <see cref="Final"/> says whether the platform can still change the answer;
/// <see cref="Execution.WaitForAsync"/> waits until it cannot.
/// </remarks>
public sealed record ExecutionResult
{
    private static readonly HashSet<CommandState> FinalStates =
        [CommandState.Succeeded, CommandState.Failed, CommandState.Expired, CommandState.Rejected];

    /// <summary>True when the command succeeded.</summary>
    public required bool Ok { get; init; }

    /// <summary>The idempotency key the call was made with, so a retry can reuse it.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>How long the SDK waited, end to end. Always known, even when nothing else is.</summary>
    public required TimeSpan ClientRtt { get; init; }

    /// <summary>The platform's id for the command, for <see cref="Execution.WaitForAsync"/> and <see cref="Execution.GetCommandAsync"/>.</summary>
    public Guid? CommandId { get; init; }

    /// <summary>Where the command is: on its way, final, or UNKNOWN; null when there was no answer.</summary>
    public CommandState? State { get; init; }

    /// <summary>True once the platform will not change this answer again.</summary>
    public bool Final { get; init; }

    /// <summary>Whether sending the same order again under a NEW key can succeed. False while UNKNOWN.</summary>
    public bool Retryable { get; init; }

    /// <summary>The same key with the same body: this is the original command, not a second one.</summary>
    public bool Duplicate { get; init; }

    /// <summary>Why it ended as it did; null when the platform gives no reason, which is what a success looks like.</summary>
    public Reason? Reason { get; init; }

    /// <summary>The reason exactly as it arrived, including one this SDK version does not know.</summary>
    public string? ReasonText { get; init; }

    /// <summary>The trading platform's own return code for the request.</summary>
    public int? Mt5Retcode { get; init; }

    /// <summary>The return code's meaning, in words.</summary>
    public string? RetcodeText { get; init; }

    /// <summary>The broker's own comment, verbatim.</summary>
    public string? CommentRaw { get; init; }

    /// <summary>The terminal's state when the command was answered.</summary>
    public string? TerminalState { get; init; }

    /// <summary>The order, deal and position the broker gave this fill.</summary>
    public Tickets Tickets { get; init; } = new();

    /// <summary>The price the broker filled at.</summary>
    public decimal? ExecPrice { get; init; }

    /// <summary>The volume the broker filled, in lots.</summary>
    public decimal? ExecVolume { get; init; }

    /// <summary>Time at the broker for this command, in milliseconds.</summary>
    public int? BrokerLatencyMs { get; init; }

    /// <summary>Time inside the platform's terminal for this command, in milliseconds.</summary>
    public int? AgentLatencyMs { get; init; }

    /// <summary>How many times the platform sent it to the broker.</summary>
    public int Attempts { get; init; }

    /// <summary>The terminal a failover moved this command from, when one did.</summary>
    public string? ReroutedFrom { get; init; }

    /// <summary>What the platform or the SDK said when the request was refused outright.</summary>
    public string? Detail { get; init; }

    /// <summary>When sending it again can work: the maintenance window's end, for a maintenance refusal.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>The venue that answered (<c>mt5</c> or <c>ctrader</c>); null until there is a result.</summary>
    public string? Venue { get; init; }

    /// <summary>The venue's own code for a refusal, verbatim (cTrader); null on the terminal, whose code is <see cref="Mt5Retcode"/>.</summary>
    public string? VenueCode { get; init; }

    /// <summary>Every deal the command made, in order (cTrader); empty on the terminal.</summary>
    public IReadOnlyList<VenueDeal> Deals { get; init; } = [];

    /// <summary>The volume filled in units of the instrument (cTrader); <see cref="ExecVolume"/> stays in lots.</summary>
    public decimal? VolumeUnits { get; init; }

    /// <summary>A close-by the venue has no equivalent for, done as two closes (cTrader).</summary>
    public bool CloseByEmulated { get; init; }

    /// <summary>A market order filled but its stop loss or take profit could not be set: the position is open without them.</summary>
    public bool ProtectionNotSet { get; init; }

    /// <summary>A result from a Command or a CommandAccepted body, whichever the call answered with.</summary>
    internal static ExecutionResult FromCommand(JsonElement body, string idempotencyKey, TimeSpan clientRtt)
    {
        var raw = body.ValueKind == JsonValueKind.Object ? body : default;
        var state = Enum(Field(raw, "state"));
        var answer = Field(raw, "result");
        var hasResult = answer.ValueKind == JsonValueKind.Object;
        var reasonText = Text(Field(answer, "reason"));
        return new ExecutionResult
        {
            // The result says, once there is one. A CommandAccepted has none: a repeat of a command
            // that already succeeded says so by its state alone.
            Ok = hasResult ? Field(answer, "ok").ValueKind == JsonValueKind.True : state == CommandState.Succeeded,
            IdempotencyKey = idempotencyKey,
            ClientRtt = clientRtt,
            CommandId = Text(Field(raw, "command_id")) is { } id && Guid.TryParse(id, CultureInfo.InvariantCulture, out var guid) ? guid : null,
            State = state,
            Final = Field(raw, "final") is { ValueKind: JsonValueKind.True or JsonValueKind.False } final
                ? final.ValueKind == JsonValueKind.True
                : state is { } s && FinalStates.Contains(s),
            // Never retryable while the outcome is unread: that is how one fill becomes two. A
            // maintenance refusal is, whatever the flag says: the consumer refused it before it ran.
            Retryable = (Field(answer, "retryable").ValueKind == JsonValueKind.True || reasonText == "maintenance")
                && state != CommandState.Unknown,
            Duplicate = Field(raw, "duplicate").ValueKind == JsonValueKind.True,
            Reason = ReasonOf(reasonText),
            ReasonText = reasonText,
            Mt5Retcode = (int?)Integer(Field(answer, "mt5_retcode")),
            RetcodeText = Text(Field(answer, "retcode_text")),
            CommentRaw = Text(Field(answer, "comment_raw")),
            TerminalState = Text(Field(answer, "terminal_state")),
            Tickets = TicketsOf(Field(answer, "tickets")),
            ExecPrice = Exact(Field(answer, "exec_price")),
            ExecVolume = Exact(Field(answer, "exec_volume")),
            BrokerLatencyMs = (int?)Integer(Field(answer, "broker_latency_ms")),
            AgentLatencyMs = (int?)Integer(Field(answer, "agent_latency_ms")),
            Attempts = (int?)Integer(Field(answer, "attempts")) ?? 0,
            ReroutedFrom = Text(Field(answer, "rerouted_from")),
            Venue = Text(Field(answer, "venue")),
            VenueCode = Text(Field(answer, "venue_code")),
            Deals = DealsOf(Field(answer, "deals")),
            VolumeUnits = Exact(Field(answer, "volume_units")),
            CloseByEmulated = Field(answer, "close_by_emulated").ValueKind == JsonValueKind.True,
            ProtectionNotSet = Field(answer, "protection_not_set").ValueKind == JsonValueKind.True,
        };
    }

    /// <summary>
    /// An answer the SDK concluded itself: no transport, no time, or a refusal. A transport error after
    /// the request was sent reads UNKNOWN and is not retryable under a new key: the order may be a
    /// position. Sending it again under the SAME key is safe, and answers with the original if it arrived.
    /// </summary>
    internal static ExecutionResult Concluded(
        Reason reason, string idempotencyKey, TimeSpan clientRtt, string detail,
        DateTimeOffset? retryAt = null, bool mayHaveReached = false)
    {
        var reached = reason == MfxHub.Reason.TransportError && mayHaveReached;
        return new ExecutionResult
        {
            Ok = false,
            IdempotencyKey = idempotencyKey,
            ClientRtt = clientRtt,
            Reason = reason,
            ReasonText = Wire.Word(reason),
            // A timeout is never retryable: the order may have been placed. A transport error before
            // the request left is, a rate limit is, and so is a maintenance refusal, once it ends.
            Retryable = !reached && reason is MfxHub.Reason.TransportError or MfxHub.Reason.RateLimited or MfxHub.Reason.Maintenance,
            State = reached ? CommandState.Unknown : null,
            Final = true,
            Detail = detail,
            RetryAt = retryAt,
        };
    }

    private static JsonElement Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static long? Integer(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    /// <summary>Prices, volumes and money are decimals as strings: a float price is a wrong price.</summary>
    private static decimal? Exact(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        try
        {
            return ExactDecimalConverter.Parse(value.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CommandState? Enum(JsonElement value) => Wire.Parse<CommandState>(Text(value));

    /// <summary>A reason this version knows, by its wire word; one it has never heard of is null and kept in ReasonText.</summary>
    private static Reason? ReasonOf(string? text) => Wire.Parse<Reason>(text);

    private static Tickets TicketsOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
            ? new Tickets(Integer(Field(value, "order")), Integer(Field(value, "deal")), Integer(Field(value, "position")))
            : new Tickets();

    private static VenueDeal[] DealsOf(JsonElement value) =>
        value.ValueKind != JsonValueKind.Array
            ? []
            : [.. value.EnumerateArray()
                .Where(d => Integer(Field(d, "deal")) is not null)
                .Select(d => new VenueDeal(Integer(Field(d, "deal"))!.Value, Integer(Field(d, "order")), Integer(Field(d, "position")),
                    Exact(Field(d, "volume_units")), Exact(Field(d, "price"))))];
}
