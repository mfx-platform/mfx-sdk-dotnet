using System.Reflection;
using System.Text.Json;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// What an execution call answers, read as an <see cref="ExecutionResult"/>, refused or not.
/// </summary>
public sealed class ExecutionResultTests
{
    private static ExecutionResult Read(string json) =>
        ExecutionResult.FromCommand(JsonDocument.Parse(json).RootElement, "k-1", TimeSpan.FromMilliseconds(12));

    /// <summary>
    /// Given a whole command that succeeded, with tickets, decimals and latencies / When it is read /
    /// Then it is ok and final, its price and volume are exact decimals, its position ticket is the
    /// one to close, and the key and the client's own time are on it.
    /// </summary>
    [Fact]
    public void A_succeeded_command_reads_whole_with_exact_decimals()
    {
        var result = Read($$$"""
            {"command_id": "{{{FakeApi.Id2}}}", "state": "SUCCEEDED", "final": true,
             "result": {"ok": true, "reason": null, "retryable": false, "mt5_retcode": 10009, "comment_raw": "Request executed",
                        "tickets": {"order": 30551, "deal": 30552, "position": 30551}, "exec_price": "1.12180", "exec_volume": "0.01",
                        "broker_latency_ms": 41, "agent_latency_ms": 3, "attempts": 1}}
            """);

        Assert.True(result.Ok);
        Assert.True(result.Final);
        Assert.Equal(CommandState.Succeeded, result.State);
        Assert.Equal(Guid.Parse(FakeApi.Id2), result.CommandId);
        Assert.Equal(1.12180m, result.ExecPrice);
        Assert.Equal("1.12180", result.ExecPrice!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(new Tickets(30551, 30552, 30551), result.Tickets);
        Assert.Equal((41, 3, 1, 10009), (result.BrokerLatencyMs, result.AgentLatencyMs, result.Attempts, result.Mt5Retcode));
        Assert.Equal(("k-1", TimeSpan.FromMilliseconds(12)), (result.IdempotencyKey, result.ClientRtt));
        Assert.Null(result.Reason);
    }

    /// <summary>
    /// Given an accepted command, an UNKNOWN one the platform marks retryable, a maintenance refusal
    /// marked not retryable, and a reason this version has never heard of / When each is read / Then
    /// the accepted one is not ok and not final, UNKNOWN is never retryable, maintenance always is, and
    /// the new reason is kept verbatim with no enum value forced on it.
    /// </summary>
    [Fact]
    public void Unknown_is_never_retryable_and_a_new_reason_is_kept_verbatim()
    {
        var accepted = Read($$"""{"command_id": "{{FakeApi.Id2}}", "state": "ACCEPTED", "duplicate": false}""");
        var unknown = Read("""{"state": "UNKNOWN", "result": {"ok": false, "retryable": true, "reason": "unknown"}}""");
        var paused = Read("""{"state": "REJECTED", "result": {"ok": false, "retryable": false, "reason": "maintenance"}}""");
        var novel = Read("""{"state": "REJECTED", "result": {"ok": false, "reason": "broker_on_fire"}}""");

        Assert.False(accepted.Ok);
        Assert.False(accepted.Final);
        Assert.False(unknown.Retryable);
        Assert.Equal(Reason.Unknown, unknown.Reason);
        Assert.True(paused.Retryable);
        Assert.True(paused.Final);
        Assert.Null(novel.Reason);
        Assert.Equal("broker_on_fire", novel.ReasonText);
    }

    /// <summary>
    /// Given a cTrader command with two deals and a fill whose protection was not set / When it is read
    /// / Then each deal is kept in order with its units and price, and the flags are true.
    /// </summary>
    [Fact]
    public void A_venues_deals_are_kept_in_order()
    {
        var result = Read("""
            {"state": "SUCCEEDED", "final": true,
             "result": {"ok": true, "venue": "ctrader", "deals": [{"deal": 7, "position": 3, "volume_units": "1000", "price": "1.1"}, {"order": 9}, {"deal": 8, "volume_units": "500"}],
                        "volume_units": "1500", "close_by_emulated": true, "protection_not_set": true}}
            """);

        Assert.Equal([7L, 8L], result.Deals.Select(d => d.Deal));
        Assert.Equal(1000m, result.Deals[0].VolumeUnits);
        Assert.Equal(1500m, result.VolumeUnits);
        Assert.Equal("ctrader", result.Venue);
        Assert.True(result.CloseByEmulated && result.ProtectionNotSet);
    }
}

/// <summary>
/// Placing an order:
/// decimals as plain text, the caller's key, and nothing invented.
/// </summary>
public sealed class PlaceOrderTests
{
    private static readonly Guid Terminal = Guid.Parse(FakeApi.Id1);
    private const string Accepted = """{"command_id": "0191f0c2-aaaa-7bbb-8ccc-000000000002", "state": "ACCEPTED", "duplicate": false}""";

    /// <summary>
    /// Given a volume with trailing zeros, one with an exponent, and stops / When an order is placed /
    /// Then each decimal goes as a JSON string of plain digits with its scale kept (0.010, 1000), the
    /// side and type are the contract's words, the key is the Idempotency-Key header, and what the
    /// caller did not give is left out of the body, the platform's defaults included.
    /// </summary>
    [Fact]
    public async Task Decimals_go_as_plain_text_and_nothing_is_invented()
    {
        using var api = new FakeApi().Answer(Accepted, 202).Answer(Accepted, 202);
        using var client = api.Client();

        var result = await client.Execution.PlaceOrderAsync(Terminal, "EURUSD.sd", Side.Sell, 0.010m, "order-1", sl: 1.2000m);
        await client.Execution.PlaceOrderAsync(Terminal, "XAUUSD", Side.Buy, decimal.Parse("1E+3", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture),
            "order-2", OrderType.StopLimit, price: 2400m, stopLimitPrice: 2399.5m);

        Assert.Equal(CommandState.Accepted, result.State);
        var first = api.Requests[0];
        Assert.Equal(("POST", $"/v1/terminals/{FakeApi.Id1}/orders", "order-1"), (first.Method, first.PathAndQuery, first.Key));
        Assert.Equal("""{"symbol":"EURUSD.sd","side":"sell","order_type":"market","volume":"0.010","sl":"1.2000"}""", first.Body);
        var second = api.Requests[1].Json!;
        Assert.Equal("1000", second["volume"]!.GetValue<string>());
        Assert.Equal("stop_limit", second["order_type"]!.GetValue<string>());
        Assert.Equal("2399.5", second["stop_limit_price"]!.GetValue<string>());
    }

    /// <summary>
    /// Given a negative volume, a negative stop and a blank key / When an order is placed / Then each is
    /// refused before anything is sent; and no public method of the SDK takes a binary floating-point
    /// number where a price, volume or amount goes, so a double cannot be passed at all.
    /// </summary>
    [Fact]
    public async Task A_bad_amount_or_a_blank_key_is_refused_and_no_amount_is_a_double()
    {
        using var api = new FakeApi();
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Execution.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, -0.01m, "k"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Execution.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, 0.01m, "k", sl: -1m));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Execution.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, 0.01m, "  "));
        Assert.Empty(api.Requests);

        var floats = typeof(MfxClient).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "MfxHub")
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)) // a property's setter is a reading, not an amount the caller sends
            .SelectMany(m => m.GetParameters().Select(p => (m, p)))
            .Where(x => (Nullable.GetUnderlyingType(x.p.ParameterType) ?? x.p.ParameterType) is var t
                && (t == typeof(double) || t == typeof(float) || t == typeof(Half)))
            .Select(x => $"{x.m.DeclaringType!.Name}.{x.m.Name}({x.p.Name})")
            .Where(name => !name.StartsWith("MfxClientOptions", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(floats);
    }

    /// <summary>
    /// Given an order answered 429 with Retry-After / When it is placed / Then the result is not ok and
    /// rate limited, retryable, and the order was sent exactly once: a write is never retried.
    /// </summary>
    [Fact]
    public async Task An_order_is_sent_once_whatever_the_answer()
    {
        using var api = new FakeApi().Answer("""{"error":{"code":"rate_limited","message":"slow down"}}""", 429, "3");
        using var client = api.Client();

        var result = await client.Execution.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, 0.01m, "k-429");

        Assert.False(result.Ok);
        Assert.Equal(Reason.RateLimited, result.Reason);
        Assert.True(result.Retryable);
        Assert.Equal("k-429", api.Only.Key);
        Assert.Empty(api.Pauses);
    }

    /// <summary>
    /// Given a comment of 31 emoji, each one code point and two UTF-16 units / When an order is placed with it on
    /// a terminal and on a trading account / Then it is sent whole: the limit counts code points, as the contract's
    /// maxLength does, not string.Length.
    /// </summary>
    [Fact]
    public async Task A_comment_of_31_code_points_is_sent()
    {
        var comment = string.Concat(Enumerable.Repeat("\U0001F600", 31));
        using var api = new FakeApi().Answer(Accepted, 202).Answer(Accepted, 202);
        using var client = api.Client();

        await client.Execution.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, 0.01m, "k-31", comment: comment);
        await client.TradingAccounts.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, 0.01m, "k-31b", comment: comment);

        Assert.Equal(62, comment.Length);
        Assert.All(api.Requests, r => Assert.Equal(comment, r.Json!["comment"]!.GetValue<string>()));
    }

    /// <summary>
    /// Given a comment of 32 code points / When an order is placed with it, on a terminal or a trading account /
    /// Then it is refused with an ArgumentException saying the limit and the count given, before any request,
    /// because the terminal would cut it without saying so.
    /// </summary>
    [Fact]
    public async Task A_comment_of_32_code_points_is_refused_before_any_request()
    {
        var comment = string.Concat(Enumerable.Repeat("\U0001F600", 32));
        using var api = new FakeApi();
        using var client = api.Client();

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => client.Execution.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, 0.01m, "k-32", comment: comment));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.TradingAccounts.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, 0.01m, "k-32b", comment: comment));

        Assert.Equal("comment is at most 31 characters; the terminal would cut a longer one (32 given)", refused.Message);
        Assert.Empty(api.Requests);
    }

    /// <summary>
    /// Given a comment for the closing deal / When a position is closed in full and in part, on a terminal and on a
    /// trading account / Then each body carries the comment as given (31 emoji, 31 code points, sent whole), and a
    /// close without one sends no comment at all, so the deal takes the position's own.
    /// </summary>
    [Fact]
    public async Task A_close_sends_its_comment_and_none_when_absent()
    {
        var comment = string.Concat(Enumerable.Repeat("\U0001F600", 31));
        using var api = new FakeApi();
        for (var i = 0; i < 6; i++)
        {
            api.Answer(Accepted, 202);
        }
        using var client = api.Client();

        await client.Execution.ClosePositionAsync(Terminal, 7, "c-1", comment: "mine-1");
        await client.Execution.ClosePositionPartialAsync(Terminal, 7, 0.01m, "c-2", comment: comment);
        await client.TradingAccounts.ClosePositionAsync(Terminal, 7, "c-3", comment: "mine-3");
        await client.TradingAccounts.ClosePositionPartialAsync(Terminal, 7, 0.01m, "c-4", comment: "mine-4");
        await client.Execution.ClosePositionAsync(Terminal, 7, "c-5");
        await client.TradingAccounts.ClosePositionPartialAsync(Terminal, 7, 0.01m, "c-6");

        Assert.Equal(["mine-1", comment, "mine-3", "mine-4"], api.Requests.Take(4).Select(r => r.Json!["comment"]!.GetValue<string>()));
        Assert.All(api.Requests.Skip(4), r => Assert.False((r.Json ?? []).ContainsKey("comment")));
    }

    /// <summary>
    /// Given a closing comment of 32 code points / When a position is closed in full or in part, on a terminal or a
    /// trading account / Then each throws the ArgumentException place_order throws, with the count given, before
    /// any request is sent.
    /// </summary>
    [Fact]
    public async Task A_close_comment_over_31_code_points_is_refused_before_any_request()
    {
        var comment = string.Concat(Enumerable.Repeat("\U0001F600", 32));
        using var api = new FakeApi();
        using var client = api.Client();

        var refusals = new[]
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.Execution.ClosePositionAsync(Terminal, 7, "c-1", comment: comment)),
            await Assert.ThrowsAsync<ArgumentException>(() => client.Execution.ClosePositionPartialAsync(Terminal, 7, 0.01m, "c-2", comment: comment)),
            await Assert.ThrowsAsync<ArgumentException>(() => client.TradingAccounts.ClosePositionAsync(Terminal, 7, "c-3", comment: comment)),
            await Assert.ThrowsAsync<ArgumentException>(() => client.TradingAccounts.ClosePositionPartialAsync(Terminal, 7, 0.01m, "c-4", comment: comment)),
        };

        Assert.All(refusals, r => Assert.Equal("comment is at most 31 characters; the terminal would cut a longer one (32 given)", r.Message));
        Assert.Empty(api.Requests);
    }

    /// <summary>
    /// Given the close methods of Execution and TradingAccounts / When their parameters are read / Then the full and
    /// partial closes take an optional comment, and close-by, whose body has none, does not.
    /// </summary>
    [Fact]
    public void The_full_and_partial_closes_take_a_comment_and_close_by_does_not()
    {
        foreach (var type in new[] { typeof(Execution), typeof(TradingAccounts) })
        {
            bool Takes(string method) => type.GetMethod(method)!.GetParameters()
                .Any(p => p.Name == "comment" && p.ParameterType == typeof(string) && p.IsOptional);

            Assert.True(Takes(nameof(Execution.ClosePositionAsync)), type.Name);
            Assert.True(Takes(nameof(Execution.ClosePositionPartialAsync)), type.Name);
            Assert.False(Takes(nameof(Execution.ClosePositionByAsync)), type.Name);
        }
    }
}

/// <summary>
/// Every outcome of trying to trade is a result, never an exception: refusals, maintenance, timeouts,
/// lost connections and duplicates.
/// </summary>
public sealed class ExecutionOutcomesTests
{
    private static readonly Guid Terminal = Guid.Parse(FakeApi.Id1);

    private static Task<ExecutionResult> Place(MfxClient client, string key = "k") =>
        client.Execution.PlaceOrderAsync(Terminal, "EURUSD", Side.Buy, 0.01m, key);

    /// <summary>
    /// Given a 409 refusal and a 403 maintenance refusal naming its end / When an order is placed for
    /// each / Then both return a result that is not ok: the first refused and not retryable, the second
    /// maintenance, retryable at the window's end.
    /// </summary>
    [Fact]
    public async Task A_refusal_and_a_maintenance_window_are_results()
    {
        using var api = new FakeApi().Fail(409, "state_error", "the terminal is draining")
            .Fail(403, "maintenance", "paused for maintenance", """{"ends_at": "2026-11-20T03:00:00Z"}""");
        using var client = api.Client();

        var refused = await Place(client);
        var paused = await Place(client);

        Assert.Equal((false, Reason.Refused, false), (refused.Ok, refused.Reason, refused.Retryable));
        Assert.Equal("the terminal is draining", refused.Detail);
        Assert.Equal((false, Reason.Maintenance, true), (paused.Ok, paused.Reason, paused.Retryable));
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero), paused.RetryAt);
    }

    /// <summary>
    /// Given an order that times out, one whose connection never opened, and one whose answer was lost
    /// after it was sent / When each is placed / Then each is a final result: the timeout never
    /// retryable, the unopened connection retryable, and the lost answer UNKNOWN and not retryable,
    /// because the order may already be a position.
    /// </summary>
    [Fact]
    public async Task No_answer_is_a_result_and_a_lost_one_is_unknown()
    {
        using var api = new FakeApi().Throw(new TaskCanceledException("timed out"))
            .Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused"))
            .Throw(new HttpRequestException(HttpRequestError.ResponseEnded, "dropped"));
        using var client = api.Client();

        var timedOut = await Place(client, "k-1");
        var neverSent = await Place(client, "k-2");
        var lost = await Place(client, "k-3");

        Assert.Equal((Reason.Timeout, false, true), (timedOut.Reason, timedOut.Retryable, timedOut.Final));
        Assert.Equal((Reason.TransportError, true, (CommandState?)null), (neverSent.Reason, neverSent.Retryable, neverSent.State));
        Assert.Equal((Reason.TransportError, false, (CommandState?)CommandState.Unknown), (lost.Reason, lost.Retryable, lost.State));
        Assert.All(new[] { timedOut, neverSent, lost }, r => Assert.False(r.Ok));
        Assert.Equal(3, api.Requests.Count);
    }

    /// <summary>
    /// Given a repeat of a command already final, answered with its state only / When it is placed /
    /// Then the command is read once and the original result returned, marked duplicate; when that read
    /// fails, what the platform said is returned with how to read the rest, and nothing is thrown.
    /// </summary>
    [Fact]
    public async Task A_final_duplicate_is_the_original_result()
    {
        var repeat = $$"""{"command_id": "{{FakeApi.Id2}}", "state": "SUCCEEDED", "duplicate": true}""";
        var command = "{\"command_id\": \"ID\", \"state\": \"SUCCEEDED\", \"final\": true, \"result\": {\"ok\": true, \"exec_price\": \"1.12181\", \"tickets\": {\"position\": 30551}}}".Replace("ID", FakeApi.Id2, StringComparison.Ordinal);
        using var api = new FakeApi().Answer(repeat, 202).Answer(command).Answer(repeat, 202).Fail(503, "dependency_unavailable");
        using var client = api.Client();

        var original = await Place(client, "dup");
        var unread = await Place(client, "dup");

        Assert.Equal((true, true, 1.12181m, 30551L), (original.Ok, original.Duplicate, original.ExecPrice, original.Tickets.Position));
        Assert.Equal($"/v1/commands/{FakeApi.Id2}", api.Requests[1].PathAndQuery);
        Assert.Null(api.Requests[1].Key);
        Assert.Equal((true, true, (decimal?)null), (unread.Ok, unread.Duplicate, unread.ExecPrice));
        Assert.Contains("could not be read", unread.Detail, StringComparison.Ordinal);
        Assert.Equal(4, api.Requests.Count);
    }
}

/// <summary>
/// The other commands and the reads of a command: cancelling, modifying and closing, the trailing
/// stop, reading a command and waiting for it.
/// </summary>
public sealed class ExecutionCommandsTests
{
    private static readonly Guid Terminal = Guid.Parse(FakeApi.Id1);
    private const string Accepted = """{"command_id": "0191f0c2-aaaa-7bbb-8ccc-000000000002", "state": "ACCEPTED"}""";

    /// <summary>
    /// Given a position and a pending order / When each command is sent / Then each is one request on its
    /// route with the caller's key and its body: the close-by names the opposite ticket, the partial
    /// close and the trailing distance are decimal text, and clearing the trail is a DELETE.
    /// </summary>
    [Fact]
    public async Task Each_command_is_one_request_on_its_route()
    {
        using var api = new FakeApi();
        for (var i = 0; i < 7; i++)
        {
            api.Answer(Accepted, 202);
        }
        using var client = api.Client();
        var x = client.Execution;

        await x.CancelOrderAsync(Terminal, 11, "k-1");
        await x.ModifyPositionAsync(Terminal, 12, "k-2", tp: 1.3m);
        await x.ClosePositionAsync(Terminal, 13, "k-3", deviationPoints: 5);
        await x.ClosePositionByAsync(Terminal, 14, 15, "k-4");
        await x.ClosePositionPartialAsync(Terminal, 16, 0.50m, "k-5");
        await x.SetTrailingStopAsync(Terminal, 17, 0.0020m, "k-6");
        await x.ClearTrailingStopAsync(Terminal, 17, "k-7");

        var p = $"/v1/terminals/{FakeApi.Id1}";
        Assert.Equal(
            [
                ("POST", p + "/orders/11/cancel", "k-1", ""),
                ("POST", p + "/positions/12/modify", "k-2", """{"tp":"1.3"}"""),
                ("POST", p + "/positions/13/close", "k-3", """{"deviation_points":5}"""),
                ("POST", p + "/positions/14/close-by", "k-4", """{"by_ticket":15}"""),
                ("POST", p + "/positions/16/close-partial", "k-5", """{"volume":"0.50"}"""),
                ("PUT", p + "/positions/17/trailing-stop", "k-6", """{"distance":"0.0020"}"""),
                ("DELETE", p + "/positions/17/trailing-stop", "k-7", ""),
            ],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key, r.Body)));
    }

    /// <summary>
    /// Given a pending order / When it is modified keeping its expiry, clearing it and setting it, and
    /// with nothing to change / Then a kept expiry is left out, a cleared one is sent as null, a set one as
    /// the instant, and nothing to change is refused before anything is sent, as is a position modify
    /// with neither stop.
    /// </summary>
    [Fact]
    public async Task An_expiry_is_kept_cleared_or_set()
    {
        using var api = new FakeApi().Answer(Accepted, 202).Answer(Accepted, 202).Answer(Accepted, 202);
        using var client = api.Client();
        var x = client.Execution;

        await Assert.ThrowsAsync<ArgumentException>(() => x.ModifyOrderAsync(Terminal, 9, "k"));
        await Assert.ThrowsAsync<ArgumentException>(() => x.ModifyOrderAsync(Terminal, 9, "k", expiresAt: Expiry.Keep));
        await Assert.ThrowsAsync<ArgumentException>(() => x.ModifyPositionAsync(Terminal, 9, "k"));
        Assert.Empty(api.Requests);
        await x.ModifyOrderAsync(Terminal, 9, "k-1", price: 1.1m);
        await x.ModifyOrderAsync(Terminal, 9, "k-2", expiresAt: Expiry.Clear);
        await x.ModifyOrderAsync(Terminal, 9, "k-3", sl: 0m, expiresAt: new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(["""{"price":"1.1"}""", """{"expires_at":null}"""], api.Requests.Take(2).Select(r => r.Body));
        var set = api.Requests[2].Json!;
        Assert.Equal(["sl", "expires_at"], set.Select(p => p.Key));
        Assert.Equal(("0", "2026-11-18T00:00:00+00:00"), (set["sl"]!.GetValue<string>(), set["expires_at"]!.GetValue<string>()));
    }

    /// <summary>
    /// Given a command that is EXECUTING twice and then SUCCEEDED / When it is waited for / Then it is read
    /// after 0.2 s and 0.4 s and the final result returned; a command that never ends is returned as it is
    /// when the time runs out, and a 404 on a read is a refused result.
    /// </summary>
    [Fact]
    public async Task A_command_is_waited_for_until_it_is_final()
    {
        string Command(string state, bool final) =>
            $$"""{"command_id": "{{FakeApi.Id2}}", "state": "{{state}}", "final": {{(final ? "true" : "false")}}}""";
        using var api = new FakeApi().Answer(Command("EXECUTING", false)).Answer(Command("EXECUTING", false)).Answer(Command("SUCCEEDED", true))
            .Answer(Command("EXECUTING", false)).Answer(Command("EXECUTING", false)).Fail(404, "not_found");
        using var client = api.Client();

        var done = await client.Execution.WaitForAsync(Guid.Parse(FakeApi.Id2), idempotencyKey: "k");
        var running = await client.Execution.WaitForAsync(Guid.Parse(FakeApi.Id2), TimeSpan.FromMilliseconds(100));
        var missing = await client.Execution.GetCommandAsync(Guid.Parse(FakeApi.Id3));

        Assert.Equal((CommandState.Succeeded, "k"), (done.State, done.IdempotencyKey));
        Assert.Equal(CommandState.Executing, running.State);
        Assert.Equal((false, Reason.Refused), (missing.Ok, missing.Reason));
        Assert.Equal([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(200)], api.Pauses);
    }
}

/// <summary>A terminal's account reads.</summary>
public sealed class AccountsTests
{
    private static readonly Guid Terminal = Guid.Parse(FakeApi.Id1);

    /// <summary>
    /// Given a terminal / When its account, positions, pending orders, spread and a window of deals are
    /// read / Then each is one GET on its route, the window goes as from and to in ISO 8601 with its
    /// offset, and the deals are a page with its cursor.
    /// </summary>
    [Fact]
    public async Task Each_account_read_is_one_request_and_deals_are_read_in_a_window()
    {
        using var api = new FakeApi()
            .Answer($$"""{"terminal_id": "{{FakeApi.Id1}}", "stale": false}""")
            .Answer("""{"as_of": "2026-11-17T09:00:00Z", "stale": false, "items": []}""")
            .Answer("""{"as_of": "2026-11-17T09:00:00Z", "stale": false, "items": []}""")
            .Answer($$"""{"terminal_id": "{{FakeApi.Id1}}", "items": [], "as_of": "2026-11-17T09:00:00Z"}""")
            .Answer("""{"items": [], "next_cursor": "c-2"}""");
        using var client = api.Client();
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var account = await client.Accounts.AccountAsync(Terminal);
        await client.Accounts.PositionsAsync(Terminal);
        await client.Accounts.PendingOrdersAsync(Terminal);
        await client.Accounts.SpreadAsync(Terminal);
        var deals = await client.Accounts.DealsAsync(Terminal, from, from.AddDays(7).AddMilliseconds(250), limit: 50);

        Assert.Null(account.Snapshot);
        Assert.Equal("c-2", deals.NextCursor);
        var p = $"/v1/terminals/{FakeApi.Id1}";
        Assert.Equal(
            [p + "/account", p + "/positions", p + "/orders/pending", p + "/spread",
             p + "/history/deals?from=2026-11-01T00%3A00%3A00%2B00%3A00&to=2026-11-08T00%3A00%3A00.250000%2B00%3A00&limit=50"],
            api.Requests.Select(r => r.PathAndQuery));
    }
}
