using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MfxHub.Models;

namespace MfxHub;

/// <summary>
/// Order entry for one tenant's terminals (<c>client.Execution</c>). Every method returns an
/// <see cref="ExecutionResult"/> and none throws for an outcome.
/// </summary>
/// <remarks>
/// A transport fault, a timeout, a rate limit, a refusal and a broker rejection are all outcomes of
/// trying to trade, and a caller that must catch each one to learn whether it has a position will
/// eventually place two. Execution is asynchronous: the platform answers 202 with a command id once
/// the command is persisted, and the outcome arrives on the stream or from
/// <see cref="GetCommandAsync"/>; <see cref="WaitForAsync"/> polls until the command is final.
/// The idempotency key is required and has no default; nothing is retried automatically, not a
/// timeout, not a rate limit, not a 5xx. A price, volume or distance is a <see cref="decimal"/>: a
/// <see cref="double"/> does not convert to one implicitly, so a binary float never reaches the wire.
/// What is refused before sending (a blank key, a negative decimal, nothing to modify) throws
/// <see cref="ArgumentException"/>: that is the caller's mistake, not an outcome.
/// </remarks>
public sealed class Execution
{
    private readonly Commands _commands;

    internal Execution(MfxClient client) => _commands = new Commands(client, "terminals");

    /// <summary>
    /// Places a market or pending order. <paramref name="symbol"/> is the broker's own name: MFX Hub never
    /// renames an instrument. A <see cref="OrderType.StopLimit"/> order has two prices:
    /// <paramref name="price"/> is where it wakes up and <paramref name="stopLimitPrice"/> the limit
    /// order placed when it does; the platform refuses one without the other, as a result.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="symbol">The broker's name for the instrument.</param>
    /// <param name="side">Buy or sell.</param>
    /// <param name="volume">The volume in lots, sent as plain decimal text (never an exponent).</param>
    /// <param name="idempotencyKey">The caller's key: a retry with the same key answers the original command.</param>
    /// <param name="orderType">Market by default.</param>
    /// <param name="price">The pending order's price.</param>
    /// <param name="stopLimitPrice">A stop-limit order's limit price.</param>
    /// <param name="sl">The stop loss.</param>
    /// <param name="tp">The take profit.</param>
    /// <param name="deviationPoints">The deviation allowed, in points; the platform's default when null.</param>
    /// <param name="expiresAt">When a pending order expires; good till cancelled when null.</param>
    /// <param name="magic">The order's magic number.</param>
    /// <param name="comment">
    /// The order's comment, at most 31 characters counted as Unicode code points (the contract's maxLength): the terminal
    /// cuts a longer one without saying so, so a longer one throws <see cref="ArgumentException"/> here, before
    /// anything is sent.
    /// </param>
    /// <param name="cancellationToken">Cancels the call; a cancelled call is not an outcome and throws.</param>
    /// <returns>The result: accepted with its command, or why not.</returns>
    public Task<ExecutionResult> PlaceOrderAsync(
        Guid terminalId,
        string symbol,
        Side side,
        decimal volume,
        string idempotencyKey,
        OrderType orderType = OrderType.Market,
        decimal? price = null,
        decimal? stopLimitPrice = null,
        decimal? sl = null,
        decimal? tp = null,
        int? deviationPoints = null,
        DateTimeOffset? expiresAt = null,
        int? magic = null,
        string? comment = null,
        CancellationToken cancellationToken = default) =>
        _commands.PlaceOrderAsync(terminalId, symbol, side, volume, idempotencyKey, orderType, price, stopLimitPrice, sl, tp,
            deviationPoints, expiresAt, magic, comment, cancellationToken);

    /// <summary>Cancels a pending order.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="ticket">The pending order's ticket.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> CancelOrderAsync(Guid terminalId, long ticket, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _commands.CancelOrderAsync(terminalId, ticket, idempotencyKey, cancellationToken);

    /// <summary>Moves a position's stop loss, its take profit, or both; at least one is required.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="ticket">The position's ticket.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="sl">The new stop loss.</param>
    /// <param name="tp">The new take profit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ModifyPositionAsync(
        Guid terminalId, long ticket, string idempotencyKey, decimal? sl = null, decimal? tp = null, CancellationToken cancellationToken = default) =>
        _commands.ModifyPositionAsync(terminalId, ticket, idempotencyKey, sl, tp, cancellationToken);

    /// <summary>
    /// Amends a pending order's price, stops or expiry; at least one must change. What is not given keeps
    /// the order's present value; a level of 0 clears a stop or a take profit. The expiry has three
    /// states: <see cref="Expiry.Keep"/> (the default) keeps the order's own, <see cref="Expiry.Clear"/>
    /// makes it good till cancelled, and <see cref="Expiry.At"/> sets it.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="ticket">The pending order's ticket.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="price">The new price.</param>
    /// <param name="stopLimitPrice">A stop-limit order's new limit price.</param>
    /// <param name="sl">The new stop loss.</param>
    /// <param name="tp">The new take profit.</param>
    /// <param name="expiresAt">The expiry: kept when null or <see cref="Expiry.Keep"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ModifyOrderAsync(
        Guid terminalId, long ticket, string idempotencyKey, decimal? price = null, decimal? stopLimitPrice = null,
        decimal? sl = null, decimal? tp = null, Expiry? expiresAt = null, CancellationToken cancellationToken = default) =>
        _commands.ModifyOrderAsync(terminalId, ticket, idempotencyKey, price, stopLimitPrice, sl, tp, expiresAt, cancellationToken);

    /// <summary>Closes a position in full.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="ticket">The position's ticket.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="deviationPoints">The deviation allowed, in points.</param>
    /// <param name="comment">
    /// Written on the closing deal (<c>Deal.comment</c>), so a caller can tell its own closes apart; without one the
    /// deal takes the position's own comment and none is sent. At most 31 characters counted as Unicode code points
    /// (the contract's maxLength): the terminal cuts a longer one without saying so, so a longer one throws
    /// <see cref="ArgumentException"/> here, before anything is sent.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ClosePositionAsync(
        Guid terminalId, long ticket, string idempotencyKey, int? deviationPoints = null, string? comment = null,
        CancellationToken cancellationToken = default) =>
        _commands.ClosePositionAsync(terminalId, ticket, idempotencyKey, deviationPoints, comment, cancellationToken);

    /// <summary>
    /// Closes a position against an opposite one in the same symbol (hedging accounts): the smaller
    /// disappears, the larger is reduced by its volume, and the spread is paid once. The result has no
    /// <see cref="ExecutionResult.ExecPrice"/>, because a close-by has no single price.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="ticket">The position being closed.</param>
    /// <param name="byTicket">The opposite position.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ClosePositionByAsync(
        Guid terminalId, long ticket, long byTicket, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _commands.ClosePositionByAsync(terminalId, ticket, byTicket, idempotencyKey, cancellationToken);

    /// <summary>Closes part of a position: the volume to close, inside the symbol's step.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="ticket">The position's ticket.</param>
    /// <param name="volume">The volume to close, in lots.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="deviationPoints">The deviation allowed, in points.</param>
    /// <param name="comment">
    /// Written on the closing deal (<c>Deal.comment</c>), so a caller can tell its own closes apart; without one the
    /// deal takes the position's own comment and none is sent. At most 31 characters counted as Unicode code points
    /// (the contract's maxLength): the terminal cuts a longer one without saying so, so a longer one throws
    /// <see cref="ArgumentException"/> here, before anything is sent.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ClosePositionPartialAsync(
        Guid terminalId, long ticket, decimal volume, string idempotencyKey, int? deviationPoints = null, string? comment = null,
        CancellationToken cancellationToken = default) =>
        _commands.ClosePositionPartialAsync(terminalId, ticket, volume, idempotencyKey, deviationPoints, comment, cancellationToken);

    /// <summary>
    /// Keeps the position's stop loss <paramref name="distance"/> behind the best price it reaches,
    /// only ever tighter. Setting again replaces the distance; a stop loss set by anyone else ends it.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="ticket">The position's ticket.</param>
    /// <param name="distance">A price difference above zero (0.0020 on EURUSD is twenty pips).</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> SetTrailingStopAsync(
        Guid terminalId, long ticket, decimal distance, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _commands.SetTrailingStopAsync(terminalId, ticket, distance, idempotencyKey, cancellationToken);

    /// <summary>Removes the position's trailing stop; the stop loss stays where the trail left it.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="ticket">The position's ticket.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ClearTrailingStopAsync(Guid terminalId, long ticket, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _commands.ClearTrailingStopAsync(terminalId, ticket, idempotencyKey, cancellationToken);

    /// <summary>
    /// One command and its result. Reading is on the execution boundary too: a caller checking what
    /// happened to an order never has to catch an exception to learn that it cannot tell. Another
    /// tenant's command is a 404, as a refused result.
    /// </summary>
    /// <param name="commandId">The command.</param>
    /// <param name="idempotencyKey">The key to carry on the result; nothing is sent with it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> GetCommandAsync(Guid commandId, string idempotencyKey = "", CancellationToken cancellationToken = default) =>
        _commands.GetCommandAsync(commandId, idempotencyKey, cancellationToken);

    /// <summary>
    /// Polls until the command is final or <paramref name="timeout"/> has passed on the client's clock,
    /// 0.2 s between reads, doubling to 2 s. Returns the last answer either way: a command still
    /// EXECUTING when the time runs out is reported as it is. A result that is not final is never a
    /// licence to send again: an UNKNOWN command may already be a position.
    /// </summary>
    /// <param name="commandId">The command.</param>
    /// <param name="timeout">How long to wait; 30 s when null.</param>
    /// <param name="idempotencyKey">The key to carry on the result.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The last result.</returns>
    public Task<ExecutionResult> WaitForAsync(
        Guid commandId, TimeSpan? timeout = null, string idempotencyKey = "", CancellationToken cancellationToken = default) =>
        _commands.WaitForAsync(commandId, timeout, idempotencyKey, cancellationToken);
}

/// <summary>
/// A pending order's expiry as an amend sets it: kept as it is, cleared to good till cancelled, or set
/// to a time. Three states, because a plain null could not tell "keep" from "clear".
/// </summary>
public sealed record Expiry
{
    private Expiry(DateTimeOffset? at, bool keep) => (Time, IsKeep) = (at, keep);

    /// <summary>Keeps the order's own expiry.</summary>
    public static Expiry Keep { get; } = new(null, keep: true);

    /// <summary>Clears the expiry: good till cancelled.</summary>
    public static Expiry Clear { get; } = new(null, keep: false);

    /// <summary>Expires at <paramref name="time"/>.</summary>
    /// <param name="time">When the order expires.</param>
    /// <returns>The expiry.</returns>
    public static Expiry At(DateTimeOffset time) => new(time, keep: false);

    /// <summary>A time converts to an expiry at that time.</summary>
    /// <param name="time">When the order expires.</param>
    public static implicit operator Expiry(DateTimeOffset time) => At(time);

    /// <summary>The time it expires at; null when it is kept or cleared.</summary>
    public DateTimeOffset? Time { get; }

    /// <summary>True when the order's own expiry is kept.</summary>
    public bool IsKeep { get; }
}

/// <summary>
/// One implementation of every command for the two kinds of owner the API routes them through: a
/// terminal (<c>/terminals/{id}/...</c>) and a trading account (<c>/accounts/{id}/...</c>), which take
/// the same bodies under the same rules.
/// </summary>
internal sealed class Commands(MfxClient client, string noun)
{
    private static readonly TimeSpan PollFrom = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PollCap = TimeSpan.FromSeconds(2);

    private string Path(Guid owner, string rest) => $"/{noun}/{Wire.Id(owner)}{rest}";

    private static string Ticket(long ticket) => ticket.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// One decimal as the contract wants it: plain digits, no exponent, finite and not below zero. A
    /// negative one is refused here rather than by the edge.
    /// </summary>
    internal static string Amount(decimal value, [CallerArgumentExpression(nameof(value))] string name = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, name);
        return Wire.Decimal(value);
    }

    private static string? Amount(decimal? value, [CallerArgumentExpression(nameof(value))] string name = "") =>
        value is { } exact ? Amount(exact, name) : null;

    /// <summary>The longest comment the terminal keeps whole (the contract's maxLength), in characters.</summary>
    internal const int CommentMax = 31;

    /// <summary>
    /// An order's or a close's comment as sent, refused here when it is longer than <see cref="CommentMax"/>.
    /// Counted in Unicode code points, as the contract's maxLength counts, so an emoji (two UTF-16
    /// units) is one character, not <see cref="string.Length"/>'s two.
    /// </summary>
    internal static string? Comment(string? comment)
    {
        if (comment is null)
        {
            return null;
        }
        var count = comment.EnumerateRunes().Count();
        if (count > CommentMax)
        {
#pragma warning disable CA2208 // the message is fixed, word for word; a parameter name would add to it
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"comment is at most {CommentMax} characters; the terminal would cut a longer one ({count} given)"));
#pragma warning restore CA2208
        }
        return comment;
    }

    public Task<ExecutionResult> PlaceOrderAsync(
        Guid owner, string symbol, Side side, decimal volume, string idempotencyKey, OrderType orderType, decimal? price,
        decimal? stopLimitPrice, decimal? sl, decimal? tp, int? deviationPoints, DateTimeOffset? expiresAt, int? magic,
        string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var body = new OrderPlace
        {
            Symbol = symbol,
            Side = Wire.Parse<OrderPlaceSide>(Wire.Word(side))!.Value,
            Volume = Amount(volume),
            OrderType = Wire.Parse<OrderPlaceOrderType>(Wire.Word(orderType)),
            Price = Amount(price),
            StopLimitPrice = Amount(stopLimitPrice),
            Sl = Amount(sl),
            Tp = Amount(tp),
            DeviationPoints = deviationPoints,
            ExpiresAt = expiresAt,
            Magic = magic,
            Comment = Comment(comment),
        };
        return SendAsync(HttpMethod.Post, Path(owner, "/orders"), body, idempotencyKey, cancellationToken);
    }

    public Task<ExecutionResult> CancelOrderAsync(Guid owner, long ticket, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, Path(owner, $"/orders/{Ticket(ticket)}/cancel"), null, idempotencyKey, cancellationToken);

    public Task<ExecutionResult> ModifyPositionAsync(
        Guid owner, long ticket, string idempotencyKey, decimal? sl, decimal? tp, CancellationToken cancellationToken)
    {
        if (sl is null && tp is null)
        {
            throw new ArgumentException("modifying a position needs sl, tp, or both");
        }
        var body = new PositionModify { Sl = Amount(sl), Tp = Amount(tp) };
        return SendAsync(HttpMethod.Post, Path(owner, $"/positions/{Ticket(ticket)}/modify"), body, idempotencyKey, cancellationToken);
    }

    public Task<ExecutionResult> ModifyOrderAsync(
        Guid owner, long ticket, string idempotencyKey, decimal? price, decimal? stopLimitPrice, decimal? sl, decimal? tp,
        Expiry? expiresAt, CancellationToken cancellationToken)
    {
        var expiry = expiresAt ?? Expiry.Keep;
        if (price is null && stopLimitPrice is null && sl is null && tp is null && expiry.IsKeep)
        {
            throw new ArgumentException("modifying an order needs price, stopLimitPrice, sl, tp, or expiresAt");
        }
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        Put(body, "price", Amount(price));
        Put(body, "stop_limit_price", Amount(stopLimitPrice));
        Put(body, "sl", Amount(sl));
        Put(body, "tp", Amount(tp));
        if (!expiry.IsKeep)
        {
            // An explicit null clears the expiry; the generated model would leave a null out.
            body["expires_at"] = expiry.Time is { } at ? Wire.Instant(at) : null;
        }
        return SendAsync(HttpMethod.Post, Path(owner, $"/orders/{Ticket(ticket)}/modify"), new Literal(body), idempotencyKey, cancellationToken);
    }

    private static void Put(Dictionary<string, object?> body, string name, string? value)
    {
        if (value is not null)
        {
            body[name] = value;
        }
    }

    public Task<ExecutionResult> ClosePositionAsync(
        Guid owner, long ticket, string idempotencyKey, int? deviationPoints, string? comment, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, Path(owner, $"/positions/{Ticket(ticket)}/close"),
            new PositionClose { DeviationPoints = deviationPoints, Comment = Comment(comment) }, idempotencyKey, cancellationToken);

    public Task<ExecutionResult> ClosePositionByAsync(
        Guid owner, long ticket, long byTicket, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, Path(owner, $"/positions/{Ticket(ticket)}/close-by"),
            new PositionCloseBy { ByTicket = byTicket }, idempotencyKey, cancellationToken);

    public Task<ExecutionResult> ClosePositionPartialAsync(
        Guid owner, long ticket, decimal volume, string idempotencyKey, int? deviationPoints, string? comment,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, Path(owner, $"/positions/{Ticket(ticket)}/close-partial"),
            new PositionClosePartial { Volume = Amount(volume), DeviationPoints = deviationPoints, Comment = Comment(comment) },
            idempotencyKey, cancellationToken);

    public Task<ExecutionResult> SetTrailingStopAsync(
        Guid owner, long ticket, decimal distance, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, Path(owner, $"/positions/{Ticket(ticket)}/trailing-stop"),
            new TrailingStopSet { Distance = Amount(distance) }, idempotencyKey, cancellationToken);

    public Task<ExecutionResult> ClearTrailingStopAsync(Guid owner, long ticket, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, Path(owner, $"/positions/{Ticket(ticket)}/trailing-stop"), null, idempotencyKey, cancellationToken);

    public Task<ExecutionResult> GetCommandAsync(Guid commandId, string idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(new MfxRequest(HttpMethod.Get, "/commands/" + Wire.Id(commandId)), idempotencyKey ?? string.Empty, write: false, cancellationToken);

    public async Task<ExecutionResult> WaitForAsync(Guid commandId, TimeSpan? timeout, string idempotencyKey, CancellationToken cancellationToken)
    {
        var clock = client.Clock;
        var started = clock.GetTimestamp();
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var wait = PollFrom;
        var result = await GetCommandAsync(commandId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        while (!result.Final && clock.GetElapsedTime(started) < limit)
        {
            await client.PauseAsync(wait < PollCap ? wait : PollCap, cancellationToken).ConfigureAwait(false);
            wait *= 2;
            result = await GetCommandAsync(commandId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private Task<ExecutionResult> SendAsync(HttpMethod method, string path, object? body, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = Keys.Required(idempotencyKey);
        var bytes = body switch
        {
            null => (ReadOnlyMemory<byte>?)null,
            Literal literal => JsonSerializer.SerializeToUtf8Bytes(literal.Body, MfxJson.Requests),
            _ => Wire.Body(body),
        };
        return RunAsync(new MfxRequest(method, path, null, bytes, key), key, write: true, cancellationToken);
    }

    /// <summary>Runs one execution call, once, and turns everything it can do into a result.</summary>
    private async Task<ExecutionResult> RunAsync(MfxRequest request, string idempotencyKey, bool write, CancellationToken cancellationToken)
    {
        var clock = client.Clock;
        var started = clock.GetTimestamp();
        TimeSpan Elapsed() => clock.GetElapsedTime(started);
        var what = $"{request.Method.Method} {request.Path}";
        MfxAnswer answer;
        try
        {
            answer = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (MfxTimeoutException timedOut)
        {
            // Never retried, by the SDK or by anyone: the order may have reached the broker, and
            // deal-history reconciliation is the only safe recovery.
            return ExecutionResult.Concluded(Reason.Timeout, idempotencyKey, Elapsed(), $"{what}: {timedOut.Message}");
        }
        catch (MfxTransportException failed)
        {
            return ExecutionResult.Concluded(Reason.TransportError, idempotencyKey, Elapsed(), $"{what}: {failed.Message}",
                mayHaveReached: failed.MayHaveReached);
        }
        if (ApiErrors.For(answer, what, MfxClient.RetryAfter(answer.Headers)) is { } failure)
        {
            return failure switch
            {
                MaintenanceException paused => ExecutionResult.Concluded(Reason.Maintenance, idempotencyKey, Elapsed(), paused.Message, paused.EndsAt),
                RateLimitException limited => ExecutionResult.Concluded(Reason.RateLimited, idempotencyKey, Elapsed(), limited.Message),
                _ => ExecutionResult.Concluded(Reason.Refused, idempotencyKey, Elapsed(), failure.Message),
            };
        }
        var result = ExecutionResult.FromCommand(BodyOf(answer), idempotencyKey, Elapsed());
        if (!(write && result.Duplicate && result.Final && result.CommandId is { } commandId))
        {
            return result;
        }
        // A repeat of a command that is already final is answered with its state only: read the
        // command once, so the caller gets the original result (price, tickets, reason) and not an
        // empty one. One extra read, only on a final repeat.
        var original = await GetCommandAsync(commandId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (original.CommandId is not null && original.State is not null)
        {
            return original with { Duplicate = true, ClientRtt = Elapsed() };
        }
        // DECISION: the read failed. Report what the platform did say (the state, ok from
        // it, duplicate) and how to read the rest. Never UNKNOWN: the outcome is known.
        return result with
        {
            ClientRtt = Elapsed(),
            Detail = $"the original result could not be read ({original.Detail}); GetCommandAsync({Wire.Id(commandId)}) reads it",
        };
    }

    /// <summary>The answer as JSON: 202 gives a CommandAccepted, 200 a whole Command; anything unreadable reads as nothing.</summary>
    private static JsonElement BodyOf(MfxAnswer answer)
    {
        try
        {
            using var document = JsonDocument.Parse(answer.Body.Length == 0 ? "{}"u8.ToArray() : answer.Body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>A body written as it is, nulls included.</summary>
    private sealed record Literal(Dictionary<string, object?> Body);
}
