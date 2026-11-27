using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MfxHub;

/// <summary>The connection ended. <see cref="Code"/> is the WebSocket close code, <see cref="Reason"/> the platform's words.</summary>
public sealed class StreamClosedException : MfxException
{
    /// <summary>A close with no code: the connection dropped.</summary>
    public StreamClosedException() : this(1006) { }

    /// <summary>A close with its code and the platform's reason.</summary>
    public StreamClosedException(int code, string reason = "")
        : base(reason.Length > 0 ? $"stream closed {code}: {reason}" : $"stream closed {code}")
    {
        Code = code;
        Reason = reason;
    }

    /// <summary>A close described by <paramref name="message"/>.</summary>
    public StreamClosedException(string message) : base(message) => Code = 1006;

    /// <summary>A close described by <paramref name="message"/>, caused by <paramref name="inner"/>.</summary>
    public StreamClosedException(string message, Exception inner) : base(message, inner) => Code = 1006;

    /// <summary>The WebSocket close code; 1006 when the connection dropped without one.</summary>
    public int Code { get; }

    /// <summary>The platform's reason, as it was written.</summary>
    public string Reason { get; } = string.Empty;
}

/// <summary>
/// What the stream needs of a WebSocket, and nothing more: narrow, so the reconnect and gap logic
/// can be tested against a socket that closes exactly when a test says.
/// </summary>
public interface IStreamConnection : IAsyncDisposable
{
    /// <summary>Sends one text frame.</summary>
    Task SendAsync(string message, CancellationToken cancellationToken);

    /// <summary>The next text frame; throws <see cref="StreamClosedException"/> when the connection has ended.</summary>
    Task<string> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Closes the connection.</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}

/// <summary>Opens one connection: given the URL and the signed upgrade headers, a live socket.</summary>
public delegate Task<IStreamConnection> StreamConnector(
    Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);

/// <summary>How a stream behaves. Every member has a default.</summary>
public sealed class StreamOptions
{
    /// <summary>How the connection is opened; a <see cref="ClientWebSocket"/> by default.</summary>
    public StreamConnector? Connect { get; init; }

    /// <summary>The stream's URL; derived from the client's base URL by default (<c>api.</c> becomes <c>ws.</c>).</summary>
    public Uri? Url { get; init; }

    /// <summary>The most connections opened after the first; unlimited by default.</summary>
    public int? MaxReconnects { get; init; }
}

/// <summary>Builds the channel keys the platform accepts, from ids rather than names.</summary>
/// <remarks>
/// Brokers rename servers and symbols, and a subscription keyed on a name silently stops matching
/// when they do, so a channel is keyed by id and built here rather than formatted by hand.
/// </remarks>
public static partial class Channel
{
    /// <summary>Every platform event for this tenant.</summary>
    public const string Events = "events";

    [GeneratedRegex(
        "^(md:[0-9a-f-]{36}:[^:]+|candles:[0-9a-f-]{36}:[^:]+:(M1|M5|M15|M30|H1|H4|D1|W1|MN1)|events"
        + "|results:[0-9a-f-]{36}|account:[0-9a-f-]{36}|ea:[0-9a-f-]{36}|book:[0-9a-f-]{36}:[^:]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    /// <summary>Ticks for one symbol on one server, in the broker's own name for it.</summary>
    public static string Md(Guid serverId, string symbol) => Checked($"md:{serverId}:{symbol}");

    /// <summary>Closed candles; only closed ones are published.</summary>
    public static string Candles(Guid serverId, string symbol, string timeframe) =>
        Checked($"candles:{serverId}:{symbol}:{timeframe}");

    /// <summary>Execution results for one terminal: where a command is finally answered.</summary>
    public static string Results(Guid terminalId) => Checked($"results:{terminalId}");

    /// <summary>One terminal's live account: a snapshot, then marks.</summary>
    public static string Account(Guid terminalId) => Checked($"account:{terminalId}");

    /// <summary>What the tenant's own expert on this terminal writes. Replayed up to 15 minutes on resume.</summary>
    public static string Ea(Guid terminalId) => Checked($"ea:{terminalId}");

    /// <summary>The symbol's order book as one of the tenant's terminals reads it.</summary>
    public static string Book(Guid serverId, string symbol) => Checked($"book:{serverId}:{symbol}");

    /// <summary>The key itself, refused when the platform would refuse it.</summary>
    /// <exception cref="ArgumentException">Not a channel key the platform accepts.</exception>
    public static string Checked(string channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return Shape().IsMatch(channel)
            ? channel
            : throw new ArgumentException(
                $"'{channel}' is not a channel key the platform accepts: ids are uuids and a symbol carries no colon",
                nameof(channel));
    }

    /// <summary>
    /// These replay on resume. Ticks and candles are deliberately not replayed (a stale tick is worse
    /// than none), and a book answers every subscribe with the newest one.
    /// </summary>
    internal static bool Replays(string channel) =>
        channel == Events || channel.StartsWith("results:", StringComparison.Ordinal)
        || channel.StartsWith("account:", StringComparison.Ordinal) || channel.StartsWith("ea:", StringComparison.Ordinal);
}

/// <summary>What the stream delivers: a message, a resync, or one channel's refusal.</summary>
public abstract record StreamEvent(string Channel);

/// <summary>One message on one channel.</summary>
/// <param name="Channel">The channel it arrived on.</param>
/// <param name="Seq">Its sequence number on the channel; null on a channel that has none.</param>
/// <param name="Type">The event type on <c>events</c> and <c>results</c>; null on a tick or a candle.</param>
/// <param name="Data">The payload, as the platform wrote it.</param>
/// <param name="Base">On <c>account:</c> only: the snapshot's <c>seq</c> this message applies to.</param>
/// <remarks>
/// There is deliberately no "replayed" flag: a resume replays messages the caller never saw and the
/// protocol marks no end to the replay, so <see cref="Seq"/> is the ground truth to reconcile against.
/// </remarks>
public sealed record StreamMessage(
    string Channel, long? Seq, string? Type, IReadOnlyDictionary<string, JsonElement> Data, long? Base = null)
    : StreamEvent(Channel);

/// <summary>
/// The requested position is older than the replay window: read current state over REST. Delivered,
/// not thrown: it is the platform being honest, and the remedy is a read, not a retry.
/// </summary>
public sealed record Resync(string Channel) : StreamEvent(Channel);

/// <summary>
/// The platform refused one channel; every other channel is unaffected. The subscription is dropped,
/// so a reconnect does not ask for it again. Delivered, not thrown.
/// </summary>
/// <param name="Channel">The refused channel.</param>
/// <param name="Code">The platform's code, such as <c>not_found</c> or <c>forbidden</c>.</param>
/// <param name="Message">The platform's sentence.</param>
/// <param name="Details">What the platform said about it.</param>
public sealed record ChannelRefused(
    string Channel, string Code, string Message, IReadOnlyDictionary<string, JsonElement> Details)
    : StreamEvent(Channel);

/// <summary>What the stream missed and how often it had to come back. A snapshot; it never changes under the caller.</summary>
/// <param name="Reconnects">Connections opened after the first.</param>
/// <param name="Dropped">Messages the sequence numbers prove were not delivered, over every channel.</param>
/// <param name="Resyncs">Channels told to resync because the replay window had passed.</param>
/// <param name="SlowConsumerDrops">Times the platform closed the stream for falling behind (4008).</param>
/// <param name="LifetimeReconnects">Times the connection's lifetime was reached (4009). Expected, not a fault.</param>
/// <param name="Refused">Channels the platform refused.</param>
public sealed record StreamStats(
    int Reconnects = 0, long Dropped = 0, int Resyncs = 0, int SlowConsumerDrops = 0,
    int LifetimeReconnects = 0, int Refused = 0);

/// <summary>The multiplexed stream: ticks, candles, events and results on one signed WebSocket.</summary>
/// <remarks>
/// <para>
/// A reconnect resumes what can be resumed and counts the rest: events, results, account and
/// expert channels carry a per-channel <c>seq</c> and are resumed from the last one seen; ticks and
/// candles subscribe again, and their gap is counted rather than hidden.
/// </para>
/// <para>
/// Nothing is buffered on the caller's behalf: a read takes the next frame from the socket, so a slow
/// consumer meets real backpressure and is closed by the platform with 4008, which is counted.
/// Close code 4001 (re-authenticate) throws <see cref="AuthException"/>: every reconnect would meet
/// the same wall, and a client spinning on a revoked key looks like an outage.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The shared surface's name (MfxStream in spec/surface.json); it is the stream of the API, not a System.IO.Stream.")]
public sealed class MfxStream : IAsyncDisposable, IAsyncEnumerable<StreamEvent>
{
    /// <summary>Re-authenticate: the key was rotated, expired or revoked. Reconnecting cannot help.</summary>
    public const int CloseReauthenticate = 4001;

    /// <summary>The platform closed the stream for falling behind.</summary>
    public const int CloseSlowConsumer = 4008;

    /// <summary>The connection reached its lifetime.</summary>
    public const int CloseLifetime = 4009;

    private readonly MfxClient _client;
    private readonly StreamConnector _connect;
    private readonly int? _maxReconnects;
    private readonly List<string> _order = [];
    private readonly Dictionary<string, long?> _lastSeq = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closing = new();
    private IStreamConnection? _connection;
    private volatile bool _closed;

    internal MfxStream(MfxClient client, StreamOptions? options)
    {
        _client = client;
        _connect = options?.Connect ?? WebSocketConnection.ConnectAsync;
        Url = options?.Url ?? UrlBeside(client.BaseUrl);
        _maxReconnects = options?.MaxReconnects;
    }

    /// <summary>The stream's URL.</summary>
    public Uri Url { get; }

    /// <summary>What has been missed so far.</summary>
    public StreamStats Stats { get; private set; } = new();

    /// <summary>The channels currently subscribed, in the order they were asked for.</summary>
    public IReadOnlyList<string> Channels => [.. _order];

    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(_client.Signer.HeadersFor("GET", Url.AbsolutePath, []), StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = MfxClient.UserAgent,
        };
        _connection = await _connect(Url, headers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes to one channel; the platform acknowledges it on the stream. Subscribing twice is not
    /// an error.
    /// </summary>
    public async Task SubscribeAsync(string channel, CancellationToken cancellationToken = default)
    {
        Channel.Checked(channel);
        if (_lastSeq.TryAdd(channel, null))
        {
            _order.Add(channel);
        }
        await SendAsync(new JsonObject { ["op"] = "subscribe", ["ch"] = channel }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the platform to bring one channel up to date from the last <c>seq</c> seen. On a channel
    /// that does not replay, or one with no position yet, this subscribes instead.
    /// </summary>
    public async Task ResumeAsync(string channel, CancellationToken cancellationToken = default)
    {
        Channel.Checked(channel);
        if (!_lastSeq.TryGetValue(channel, out var last) || last is null || !Channel.Replays(channel))
        {
            await SubscribeAsync(channel, cancellationToken).ConfigureAwait(false);
            return;
        }
        await SendAsync(new JsonObject { ["op"] = "resume", ["ch"] = channel, ["after_seq"] = last }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops watching a channel, and stops subscribing to it again after a reconnect.</summary>
    public async Task UnsubscribeAsync(string channel, CancellationToken cancellationToken = default)
    {
        if (_lastSeq.Remove(channel))
        {
            _order.Remove(channel);
        }
        await SendAsync(new JsonObject { ["op"] = "unsubscribe", ["ch"] = channel }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The next event, reconnecting underneath as needed; null when the stream is over (closed by the
    /// caller, or its reconnects are spent).
    /// </summary>
    /// <exception cref="AuthException">The platform closed with 4001: the key no longer signs.</exception>
    /// <exception cref="MfxApiException">The platform sent an error about the connection itself.</exception>
    public async Task<StreamEvent?> NextAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        while (true)
        {
            var connection = _connection;
            if (connection is null || _closed)
            {
                return null;
            }
            string raw;
            try
            {
                raw = await connection.ReceiveAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_closed)
            {
                return null;
            }
            catch (StreamClosedException closed)
            {
                if (_closed || !await ReopenAsync(closed, linked.Token).ConfigureAwait(false))
                {
                    return null;
                }
                continue;
            }
            if (Interpret(raw) is { } streamEvent)
            {
                return streamEvent;
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        while (await NextAsync(cancellationToken).ConfigureAwait(false) is { } streamEvent)
        {
            yield return streamEvent;
        }
    }

    /// <summary>
    /// Closes the stream for good. A read that is waiting ends with nothing, and a reconnect under way
    /// is given up: closing is the caller's decision, not a drop to recover from.
    /// </summary>
    public async Task CloseAsync()
    {
        _closed = true;
        await _closing.CancelAsync().ConfigureAwait(false);
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            try
            {
                await connection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _closing.Dispose();
    }

    /// <inheritdoc/>
    public override string ToString() => $"MfxStream({Url}, channels={_order.Count})";

    private StreamEvent? Interpret(string raw)
    {
        using var frame = JsonDocument.Parse(raw);
        var message = frame.RootElement;
        var op = Text(message, "op");
        var channel = Text(message, "ch") ?? string.Empty;
        switch (op)
        {
            case "error":
                var body = message.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
                var code = (body.ValueKind == JsonValueKind.Object ? Text(body, "code") : null) ?? "stream_error";
                var text = (body.ValueKind == JsonValueKind.Object ? Text(body, "message") : null)
                    ?? $"stream error on {(channel.Length > 0 ? channel : "the connection")}";
                var details = body.ValueKind == JsonValueKind.Object ? Object(body, "details") : Empty;
                if (channel.Length > 0)
                {
                    // One channel's refusal costs that channel, and is not asked for again on reconnect.
                    if (_lastSeq.Remove(channel))
                    {
                        _order.Remove(channel);
                    }
                    Stats = Stats with { Refused = Stats.Refused + 1 };
                    return new ChannelRefused(channel, code, text, details);
                }
                // About the connection itself, typed as a call's answer is. Status 0: no HTTP answer.
                throw ApiErrors.For(code, text, 0, details);
            case "resync":
                Stats = Stats with { Resyncs = Stats.Resyncs + 1 };
                // No position to resume from any more: forgetting it stops the next reconnect asking
                // for the same dead seq.
                Forget(channel);
                return new Resync(channel);
            case "msg":
                var seq = Whole(message, "seq");
                Account(channel, seq);
                return new StreamMessage(channel, seq, Text(message, "type"), Object(message, "data"), Whole(message, "base"));
            default:
                // An ack is protocol, not data; an op added after this SDK shipped is ignored, not fatal.
                return null;
        }
    }

    /// <summary>Counts what the sequence numbers prove was not delivered.</summary>
    private void Account(string channel, long? seq)
    {
        if (seq is null || !_lastSeq.TryGetValue(channel, out var last))
        {
            return;
        }
        if (last is not null && seq > last + 1)
        {
            Stats = Stats with { Dropped = Stats.Dropped + (seq.Value - last.Value - 1) };
        }
        _lastSeq[channel] = seq;
    }

    private void Forget(string channel)
    {
        if (_lastSeq.ContainsKey(channel))
        {
            _lastSeq[channel] = null;
        }
    }

    private async Task<bool> ReopenAsync(StreamClosedException closed, CancellationToken cancellationToken)
    {
        if (closed.Code == CloseReauthenticate)
        {
            _connection = null;
            throw new AuthException(
                closed.Reason.Length > 0 ? closed.Reason : "the stream's credentials were rotated, expired or revoked",
                401, "auth_error");
        }
        if (closed.Code == CloseSlowConsumer)
        {
            Stats = Stats with { SlowConsumerDrops = Stats.SlowConsumerDrops + 1 };
        }
        else if (closed.Code == CloseLifetime)
        {
            Stats = Stats with { LifetimeReconnects = Stats.LifetimeReconnects + 1 };
        }
        if (_maxReconnects is { } most && Stats.Reconnects >= most)
        {
            _connection = null;
            return false;
        }
        Stats = Stats with { Reconnects = Stats.Reconnects + 1 };
        var old = Interlocked.Exchange(ref _connection, null);
        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }
        try
        {
            // The client's own jittered backoff, so the stream and the read path lean on MFX Hub alike.
            await _client.PauseAsync(_client.Backoff(Stats.Reconnects), cancellationToken).ConfigureAwait(false);
            if (_closed)
            {
                return false;
            }
            await OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_closed)
        {
            return false;
        }
        if (_closed)
        {
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        await ResubscribeAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Asks for everything again: resuming what replays, plain subscribing what does not.</summary>
    private async Task ResubscribeAsync(CancellationToken cancellationToken)
    {
        foreach (var channel in _order.ToList())
        {
            var last = _lastSeq[channel];
            if (Channel.Replays(channel) && last is not null)
            {
                await SendAsync(new JsonObject { ["op"] = "resume", ["ch"] = channel, ["after_seq"] = last }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!Channel.Replays(channel))
            {
                // Ticks and candles are never replayed, so the position means nothing across a
                // reconnect; dropping it stops the next gap being counted against a stale seq.
                Forget(channel);
            }
            await SendAsync(new JsonObject { ["op"] = "subscribe", ["ch"] = channel }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendAsync(JsonObject message, CancellationToken cancellationToken) =>
        _connection is { } connection
            ? connection.SendAsync(message.ToJsonString(), cancellationToken)
            : throw new StreamClosedException(1006, "the stream is not open");

    private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
            : null;

    private static long? Whole(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : null;

    private static IReadOnlyDictionary<string, JsonElement> Object(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : Empty;

    /// <summary>
    /// The stream URL beside the REST base: <c>api.</c> becomes <c>ws.</c>, so a client pointed at dev
    /// cannot leave its stream pointed at production.
    /// </summary>
    internal static Uri UrlBeside(Uri baseUrl)
    {
        var url = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        url = url.StartsWith("https://", StringComparison.Ordinal) ? "wss://" + url["https://".Length..]
            : url.StartsWith("http://", StringComparison.Ordinal) ? "ws://" + url["http://".Length..] : url;
        var host = url.IndexOf("://api.", StringComparison.Ordinal);
        if (host >= 0)
        {
            url = url[..host] + "://ws." + url[(host + "://api.".Length)..];
        }
        return new Uri(url.EndsWith("/stream", StringComparison.Ordinal) ? url : url + "/stream");
    }
}

/// <summary>The default connection, over <see cref="ClientWebSocket"/>.</summary>
internal sealed class WebSocketConnection(ClientWebSocket socket) : IStreamConnection
{
    public static async Task<IStreamConnection> ConnectAsync(
        Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            foreach (var (name, value) in headers)
            {
                socket.Options.SetRequestHeader(name, value);
            }
            await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
            return new WebSocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task SendAsync(string message, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);

    public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var text = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException dropped)
            {
                throw new StreamClosedException("stream closed 1006: " + dropped.Message, dropped);
            }
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new StreamClosedException((int?)received.CloseStatus ?? 1006, received.CloseStatusDescription ?? string.Empty);
            }
            text.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
            }
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed by the caller", cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Already gone: closing is done either way.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
