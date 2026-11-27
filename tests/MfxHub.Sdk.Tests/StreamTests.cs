using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The stream resumes what can be resumed after a reconnect, counts what it missed, stops when the
/// key no longer signs, and lets one refused channel cost only that channel.
/// </summary>
public sealed class StreamTests
{
    private static readonly Guid Terminal = Guid.Parse("0191f0c2-aaaa-7bbb-8ccc-000000000001");
    private static readonly Guid Server = Guid.Parse("3c2d3e4f-5a6b-7c8d-9e0f-102030405060");

    /// <summary>One scripted connection: frames to hand out, then a close, and what the SDK sent.</summary>
    private sealed class Scripted(IEnumerable<string> frames, int? closeWith) : IStreamConnection
    {
        private readonly Queue<string> _frames = new(frames);
        private readonly Channel<bool> _quiet = System.Threading.Channels.Channel.CreateUnbounded<bool>();

        public List<JsonObject> Sent { get; } = [];

        public IReadOnlyDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        public bool Closed { get; private set; }

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            Sent.Add(JsonNode.Parse(message)!.AsObject());
            return Task.CompletedTask;
        }

        public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_frames.TryDequeue(out var frame))
            {
                return frame;
            }
            if (closeWith is { } code)
            {
                throw new StreamClosedException(code, "scripted");
            }
            await _quiet.Reader.ReadAsync(cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            Closed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string Msg(string channel, long seq, string? type = "terminal.state_changed") =>
        new JsonObject { ["op"] = "msg", ["ch"] = channel, ["seq"] = seq, ["type"] = type, ["data"] = new JsonObject { ["n"] = seq } }.ToJsonString();

    private static (MfxClient Client, StreamOptions Options, List<Scripted> Opened) Over(params Scripted[] connections)
    {
        var queue = new Queue<Scripted>(connections);
        var opened = new List<Scripted>();
        var client = new MfxClient(new Uri("https://api.example.test/v1"), "mfxk_1", SigningKey.FromSeed(SigningTests.TestSeed),
            new MfxClientOptions { Pause = (_, _) => Task.CompletedTask });
        var options = new StreamOptions
        {
            MaxReconnects = 5,
            Connect = (url, headers, _) =>
            {
                var next = queue.Dequeue();
                next.Headers = headers;
                opened.Add(next);
                return Task.FromResult<IStreamConnection>(next);
            },
        };
        return (client, options, opened);
    }

    private static async Task<List<StreamEvent>> Take(MfxStream stream, int count)
    {
        var events = new List<StreamEvent>();
        while (events.Count < count && await stream.NextAsync() is { } streamEvent)
        {
            events.Add(streamEvent);
        }
        return events;
    }

    /// <summary>
    /// Given channel keys built from ids, a key the platform would refuse, and the client's base URL
    /// / When each is checked and the stream URL derived / Then the keys are the platform's shapes, the
    /// bad ones refused before anything is sent, and the stream is beside the API (api. becomes ws.,
    /// https becomes wss) with its upgrade signed over its path.
    /// </summary>
    [Fact]
    public async Task Channels_are_keyed_by_id_and_the_stream_url_sits_beside_the_api()
    {
        Assert.Equal($"md:{Server}:EURUSD.sd", Channel.Md(Server, "EURUSD.sd"));
        Assert.Equal($"candles:{Server}:EURUSD:H1", Channel.Candles(Server, "EURUSD", "H1"));
        Assert.Equal($"results:{Terminal}", Channel.Results(Terminal));
        Assert.Equal($"account:{Terminal}", Channel.Account(Terminal));
        Assert.Equal($"ea:{Terminal}", Channel.Ea(Terminal));
        Assert.Equal($"book:{Server}:EURUSD", Channel.Book(Server, "EURUSD"));
        Assert.Throws<ArgumentException>(() => Channel.Md(Server, "EUR:USD"));
        Assert.Throws<ArgumentException>(() => Channel.Candles(Server, "EURUSD", "H2"));
        Assert.Throws<ArgumentException>(() => Channel.Checked("results:terminal-one"));

        Assert.Equal(new Uri("wss://ws.dev.mfxhub.com/v1/stream"), MfxStream.UrlBeside(new Uri("https://api.dev.mfxhub.com/v1")));
        Assert.Equal(new Uri("ws://127.0.0.1:8080/v1/stream"), MfxStream.UrlBeside(new Uri("http://127.0.0.1:8080/v1/")));

        var connection = new Scripted([], null);
        var (client, options, _) = Over(connection);
        using var _ = client;
        await using var stream = await client.StreamAsync(options);
        Assert.Equal("mfxk_1", connection.Headers[Signing.KeyHeader]);
        var message = Signing.Message(connection.Headers[Signing.TimestampHeader], connection.Headers[Signing.NonceHeader], "GET", "/v1/stream", []);
        Assert.True(SigningTests.Verifies(SigningKey.FromSeed(SigningTests.TestSeed).PublicKey(), message, connection.Headers[Signing.SignatureHeader]));
        await Assert.ThrowsAsync<ArgumentException>(() => stream.SubscribeAsync("not a channel"));
    }

    /// <summary>
    /// Given events and a terminal's results subscribed, and ticks, when the connection reaches its
    /// lifetime (4009) and later the platform drops the stream for falling behind (4008) / When the
    /// stream reconnects / Then events and results resume from their own last seq, ticks subscribe
    /// again with their position forgotten, a seq gap is counted as dropped, and each close is counted.
    /// </summary>
    [Fact]
    public async Task A_reconnect_resumes_each_channel_from_its_own_seq_and_counts_the_rest()
    {
        var results = Channel.Results(Terminal);
        var ticks = Channel.Md(Server, "EURUSD");
        var first = new Scripted([Msg("events", 7), Msg(results, 20), Msg(ticks, 40, null)], CloseLifetimeCode);
        var second = new Scripted([Msg("events", 8), Msg(results, 23), Msg(ticks, 900, null)], CloseSlowCode);
        var third = new Scripted([Msg("events", 9)], null);
        var (client, options, opened) = Over(first, second, third);
        using var _ = client;
        await using var stream = await client.StreamAsync(options);
        await stream.SubscribeAsync("events");
        await stream.SubscribeAsync(results);
        await stream.SubscribeAsync(ticks);

        var events = await Take(stream, 7);

        Assert.Equal([7L, 20, 40, 8, 23, 900, 9], events.Cast<StreamMessage>().Select(m => m.Seq!.Value));
        Assert.Null(events.Cast<StreamMessage>().Single(m => m.Seq == 40).Type);
        Assert.Equal(40, events.Cast<StreamMessage>().Single(m => m.Seq == 40).Data["n"].GetInt32());
        Assert.Equal(3, opened.Count);
        Assert.Equal(
            ["""{"op":"resume","ch":"events","after_seq":7}""", $$"""{"op":"resume","ch":"{{results}}","after_seq":20}""", $$"""{"op":"subscribe","ch":"{{ticks}}"}"""],
            second.Sent.Select(s => s.ToJsonString()));
        Assert.Equal(new StreamStats(Reconnects: 2, Dropped: 2, SlowConsumerDrops: 1, LifetimeReconnects: 1), stream.Stats);
        Assert.Equal(["events", results, ticks], stream.Channels);
    }

    private const int CloseLifetimeCode = MfxStream.CloseLifetime;
    private const int CloseSlowCode = MfxStream.CloseSlowConsumer;

    /// <summary>
    /// Given a refused channel, a resync, an ack and an op this SDK does not know / When they arrive
    /// / Then the refusal is delivered and costs only its channel (not asked for again on reconnect),
    /// the resync is delivered and forgets the position, and the rest are skipped; an error that names
    /// no channel is thrown typed; and a 4001 close throws AuthException instead of reconnecting.
    /// </summary>
    [Fact]
    public async Task A_refusal_costs_one_channel_a_resync_forgets_and_a_4001_stops_the_stream()
    {
        var results = Channel.Results(Terminal);
        var first = new Scripted(
        [
            """{"op":"ack","ch":"events"}""",
            "{\"op\":\"error\",\"ch\":\"" + results + "\",\"error\":{\"code\":\"not_found\",\"message\":\"gone\",\"details\":{\"terminal_id\":\"" + Terminal + "\"}}}",
            Msg("events", 3),
            """{"op":"resync","ch":"events"}""",
            """{"op":"teleport","ch":"events"}""",
        ], MfxStream.CloseLifetime);
        var second = new Scripted([Msg("events", 100)], MfxStream.CloseReauthenticate);
        var (client, options, _) = Over(first, second);
        using var _ = client;
        await using var stream = await client.StreamAsync(options);
        await stream.SubscribeAsync("events");
        await stream.SubscribeAsync(results);

        var events = await Take(stream, 3);

        var refused = Assert.IsType<ChannelRefused>(events[0]);
        Assert.Equal((results, "not_found", "gone"), (refused.Channel, refused.Code, refused.Message));
        Assert.Equal(Terminal.ToString(), refused.Details["terminal_id"].GetString());
        Assert.Equal(3, Assert.IsType<StreamMessage>(events[1]).Seq);
        Assert.Equal("events", Assert.IsType<Resync>(events[2]).Channel);
        Assert.Equal(100, Assert.IsType<StreamMessage>(await stream.NextAsync()).Seq);
        Assert.Equal(["""{"op":"subscribe","ch":"events"}"""], second.Sent.Select(s => s.ToJsonString()));
        Assert.Equal(new StreamStats(Reconnects: 1, Resyncs: 1, LifetimeReconnects: 1, Refused: 1), stream.Stats);

        var revoked = await Assert.ThrowsAsync<AuthException>(() => stream.NextAsync());
        Assert.Equal(("auth_error", 401, "scripted"), (revoked.Code, revoked.Status, revoked.Message));

        var (other, otherOptions, _) = Over(new Scripted(["""{"op":"error","error":{"code":"tenant_disabled","message":"off"}}"""], null));
        using var __ = other;
        await using var shut = await other.StreamAsync(otherOptions);
        Assert.Equal(0, (await Assert.ThrowsAsync<TenantDisabledException>(() => shut.NextAsync())).Status);
    }

    /// <summary>
    /// Given a read waiting on a quiet stream / When the stream is closed / Then the read ends with
    /// nothing and no error, no reconnect is attempted, the connection is closed, and a stream that
    /// spends its reconnects ends rather than spinning.
    /// </summary>
    [Fact]
    public async Task Closing_ends_a_waiting_read_and_spent_reconnects_end_the_stream()
    {
        var quiet = new Scripted([], null);
        var (client, options, _) = Over(quiet);
        using var _ = client;
        var stream = await client.StreamAsync(options);
        await stream.SubscribeAsync("events");

        var waiting = stream.NextAsync();
        await Task.Delay(50);
        await stream.CloseAsync();

        Assert.Same(waiting, await Task.WhenAny(waiting, Task.Delay(2000)));
        Assert.Null(await waiting);
        Assert.True(quiet.Closed);
        Assert.Equal(0, stream.Stats.Reconnects);
        await stream.DisposeAsync();
        await Assert.ThrowsAsync<StreamClosedException>(() => stream.SubscribeAsync("events"));

        var dropping = Enumerable.Range(0, 3).Select(_ => new Scripted([], 1006)).ToArray();
        var (spent, spentOptions, _) = Over(dropping);
        using var __ = spent;
        await using var limited = await spent.StreamAsync(new StreamOptions { Connect = spentOptions.Connect, MaxReconnects = 2 });
        Assert.Null(await limited.NextAsync());
        Assert.Equal(2, limited.Stats.Reconnects);
    }
}
