using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MfxHub;

/// <summary>How an <see cref="MfxClient"/> behaves. Every member has a default.</summary>
public sealed class MfxClientOptions
{
    /// <summary>The longest a single request may take. 30 s by default.</summary>
    public TimeSpan Timeout { get; init; } = MfxClient.DefaultTimeout;

    /// <summary>How many times a rate-limited read is tried again. 3 by default.</summary>
    public int ReadRetries { get; init; } = MfxClient.DefaultReadRetries;

    /// <summary>The clock signatures and <c>Retry-After</c> dates are read from.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>
    /// How the client waits between attempts. Replace it to test, or to wait on your own scheduler.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task>? Pause { get; init; }

    /// <summary>The HTTP handler requests go through; the platform's own by default.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Makes each nonce; 16 random bytes in base64url by default.</summary>
    public Func<string>? Nonce { get; init; }

    /// <summary>A number in [0, 1) that jitters the backoff; random by default.</summary>
    public Func<double>? Random { get; init; }

    /// <summary>
    /// Told about each HTTP attempt as it ends. A listener that throws does not fail the call. The
    /// <c>MFX_CALL_LOG</c> environment variable does the same into a file, one JSON line per attempt.
    /// </summary>
    public Action<CallTiming>? OnCall { get; init; }

    /// <summary>
    /// Where the client logs. It logs one thing: a warning, once per client, the first time an answer carries
    /// <c>MFX-Service-Agreement: due</c> (see <see cref="Tenant.ServiceAgreementAsync"/>). None by default.
    /// </summary>
    public ILogger? Logger { get; init; }
}

/// <summary>What the key has left, as of the platform's last answer (<c>X-MFX-Budget-*</c>).</summary>
/// <param name="Remaining">What is left.</param>
/// <param name="Source">
/// <c>counted</c> when it is a measurement, <c>configured</c> when it is the key's allowance,
/// <c>unconfigured</c> when the key has none. A number whose source is not <c>counted</c> is a
/// ceiling, not a reading.
/// </param>
public sealed record Budget(long Remaining, string Source);

/// <summary>One HTTP attempt: what was asked, the status, and how long it took.</summary>
/// <param name="What">The call, as <c>METHOD /path</c>, without its query.</param>
/// <param name="Status">The HTTP status; null when no answer came.</param>
/// <param name="Elapsed">How long it took, on the client's own clock.</param>
/// <remarks>Each attempt is a call: a read retried after a 429 is two.</remarks>
public sealed record CallTiming(string What, int? Status, TimeSpan Elapsed);

/// <summary>One signed connection to the MFX Hub API.</summary>
/// <remarks>
/// Every request is signed with the tenant's Ed25519 key, afresh on every attempt. A read that is
/// rate limited is tried again after the platform's own <c>Retry-After</c>. A write is never retried,
/// because a retried write is a second order.
/// </remarks>
public sealed class MfxClient : IDisposable
{
    /// <summary>The default time a single request may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The default number of times a rate-limited read is tried again.</summary>
    public const int DefaultReadRetries = 3;

    /// <summary>The longest the client waits between attempts when the platform names no delay.</summary>
    internal static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(30);

    /// <summary>The <c>User-Agent</c> every request carries.</summary>
    public static readonly string UserAgent = $"mfx-sdk-dotnet/{SdkVersion.Value}";

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _basePath;
    private readonly string _origin;
    private readonly int _readRetries;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _pause;
    private readonly Func<double> _random;
    private readonly Action<CallTiming>? _onCall;
    private readonly string? _callLog;
    private Budget? _budget;
    private readonly ILogger _logger;
    private int _agreementWarned;

    private static readonly Action<ILogger, Exception?> AgreementDue = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, "ServiceAgreementDue"),
        "MFX Hub: your organisation has a Service Agreement to accept (MFX-Service-Agreement: due). Until an owner "
        + "or admin accepts it, adding terminals, attaching expert advisors, generating API keys and asking for higher "
        + "limits may be refused with ServiceAgreementNotAcceptedException; reads, orders and anything running are not "
        + "affected. client.Tenant.ServiceAgreementAsync() says what is due and where to accept it.");

    /// <summary>The environment variable naming a file to append one JSON line to for every HTTP attempt.</summary>
    public const string CallLogEnvironment = "MFX_CALL_LOG";

    /// <summary>A client for the API at <paramref name="baseUrl"/>, signing as <paramref name="keyId"/>.</summary>
    /// <param name="baseUrl">The API's base, such as <c>https://api.mfxhub.com/v1</c>.</param>
    /// <param name="keyId">The key's id.</param>
    /// <param name="key">The key's private half.</param>
    /// <param name="options">How the client behaves; the defaults when omitted.</param>
    public MfxClient(Uri baseUrl, string keyId, SigningKey key, MfxClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!baseUrl.IsAbsoluteUri || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("the base URL is an absolute http or https URL", nameof(baseUrl));
        }
        options ??= new MfxClientOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReadRetries);
        _clock = options.Clock ?? TimeProvider.System;
        Signer = new RequestSigner(keyId, key, _clock, options.Nonce);
        _base = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        _basePath = new Uri(_base).AbsolutePath.TrimEnd('/');
        _origin = baseUrl.GetLeftPart(UriPartial.Authority);
        _readRetries = options.ReadRetries;
        _pause = options.Pause ?? ((wait, token) => Task.Delay(wait, _clock, token));
        _random = options.Random ?? System.Random.Shared.NextDouble;
        _onCall = options.OnCall;
        _logger = options.Logger ?? NullLogger.Instance;
        _callLog = Environment.GetEnvironmentVariable(CallLogEnvironment) is { Length: > 0 } log ? log : null;
        _http = options.Handler is null ? new HttpClient() : new HttpClient(options.Handler, disposeHandler: false);
        _http.Timeout = options.Timeout;
        Terminals = new Terminals(this);
        Catalog = new Catalog(this);
        Farms = new Farms(this);
        Usage = new UsageReader(this);
        Maintenance = new Maintenance(this);
        Execution = new Execution(this);
        Accounts = new Accounts(this);
        TradingAccounts = new TradingAccounts(this);
        Md = new MarketData(this);
        Risk = new Risk(this);
        Webhooks = new Webhooks(this);
        ExpertAdvisors = new ExpertAdvisors(this);
        Tenant = new Tenant(this);
        Copy = new Copy(this);
        Billing = new Billing(this);
    }

    /// <summary>Billing, read only: standing, profiles, forecast, prices and invoices.</summary>
    public Billing Billing { get; }

    /// <summary>Copy Trading: leaders, followers, groups, stop-outs, requested closes, signals and the copy log.</summary>
    public Copy Copy { get; }

    /// <summary>The tenant's webhook endpoints, and <see cref="Webhooks.Verify(ReadOnlySpan{byte}, string, IEnumerable{Secret}, TimeSpan?, DateTimeOffset?)"/> for a delivery.</summary>
    public Webhooks Webhooks { get; }

    /// <summary>The tenant's Expert Advisor versions: upload, the checks' verdict and report, withdraw.</summary>
    public ExpertAdvisors ExpertAdvisors { get; }

    /// <summary>What the tenant reads about itself.</summary>
    public Tenant Tenant { get; }

    /// <summary>Risk Management: policies, protected accounts, the planner, exit plans, challenges and activity.</summary>
    public Risk Risk { get; }

    /// <summary>Market data: what this tenant has asked to be streamed, and candle and tick history.</summary>
    public MarketData Md { get; }

    /// <summary>The trading accounts over every venue (terminal and cTrader): reads, commands and the cTrader grant flow.</summary>
    public TradingAccounts TradingAccounts { get; }

    /// <summary>Order entry on a terminal: every call returns an <see cref="ExecutionResult"/>, none throws for an outcome.</summary>
    public Execution Execution { get; }

    /// <summary>Balance, positions, orders, history and spreads for one terminal's account.</summary>
    public Accounts Accounts { get; }

    /// <summary>Terminals: provision, inspect, restart, drain and retire the terminals this tenant owns.</summary>
    public Terminals Terminals { get; }

    /// <summary>The brokers, servers and symbols this tenant may use.</summary>
    public Catalog Catalog { get; }

    /// <summary>The farms the plan allows.</summary>
    public Farms Farms { get; }

    /// <summary>What this key and its tenant have used, and what is left.</summary>
    public UsageReader Usage { get; }

    /// <summary>The maintenance windows that reach this tenant, and its terminals in each.</summary>
    public Maintenance Maintenance { get; }

    /// <summary>The API's base, without a trailing slash.</summary>
    public Uri BaseUrl => new(_base);

    /// <summary>The key's id.</summary>
    public string KeyId => Signer.KeyId;

    /// <summary>The signer every request goes through.</summary>
    public RequestSigner Signer { get; }

    /// <summary>
    /// A read: tried again after a 429, up to the client's <see cref="MfxClientOptions.ReadRetries"/>,
    /// after <c>Retry-After</c> or, when the platform names no delay, a jittered backoff. A failure
    /// throws its typed <see cref="MfxApiException"/>.
    /// </summary>
    internal async Task<MfxAnswer> ReadAsync(MfxRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var answer = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (answer.Status == 429 && attempt < _readRetries)
            {
                await _pause(RetryAfter(answer.Headers) ?? Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            return Checked(answer, request);
        }
    }

    /// <summary>
    /// A write: sent once, never retried. A failure throws its typed <see cref="MfxApiException"/>.
    /// The idempotency key is the caller's: it is sent as given, a blank one is refused before
    /// anything is sent, and none is ever invented, because a key made up per attempt makes a retry
    /// by the caller a second order.
    /// </summary>
    internal async Task<MfxAnswer> WriteAsync(MfxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IdempotencyKey is { } key && string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("an idempotency key is the caller's own, and not blank", nameof(request));
        }
        return Checked(await SendAsync(request, cancellationToken).ConfigureAwait(false), request);
    }

    /// <summary>A read of <paramref name="path"/>, its answer read as <typeparamref name="T"/>.</summary>
    internal async Task<T> GetAsync<T>(string path, Query? query, CancellationToken cancellationToken)
    {
        var answer = await ReadAsync(new MfxRequest(HttpMethod.Get, path, query), cancellationToken).ConfigureAwait(false);
        return Body<T>(answer, "GET " + path);
    }

    /// <summary>A read of one page of <paramref name="path"/>.</summary>
    internal async Task<Page<T>> PageAsync<T>(string path, Query? query, CancellationToken cancellationToken) =>
        (await GetAsync<WirePage<T>>(path, query, cancellationToken).ConfigureAwait(false)).ToPage();

    /// <summary>
    /// A write of <paramref name="body"/> to <paramref name="path"/>, sent once with the caller's key,
    /// its answer read as <typeparamref name="T"/>.
    /// </summary>
    internal async Task<T> WriteAsync<T>(
        HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken cancellationToken,
        Query? query = null)
    {
        var answer = await WriteAsync(
            new MfxRequest(method, path, query, body is null ? null : Wire.Body(body), idempotencyKey),
            cancellationToken).ConfigureAwait(false);
        return Body<T>(answer, $"{method.Method} {path}");
    }

    /// <summary>A write whose answer has no body the caller needs (a 204).</summary>
    internal async Task WriteAsync(
        HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken cancellationToken,
        Query? query = null) =>
        await WriteAsync(
            new MfxRequest(method, path, query, body is null ? null : Wire.Body(body), idempotencyKey),
            cancellationToken).ConfigureAwait(false);

    /// <summary>The answer's body as <typeparamref name="T"/>, read as the API writes it (<see cref="MfxJson.Options"/>).</summary>
    internal static T Body<T>(MfxAnswer answer, string what) =>
        JsonSerializer.Deserialize<T>(answer.Body, MfxJson.Options)
            ?? throw new JsonException($"{what} answered {answer.Status} with no {typeof(T).Name}");

    private static MfxAnswer Checked(MfxAnswer answer, MfxRequest request) =>
        ApiErrors.For(answer, $"{request.Method.Method} {Relative(request.Path)}", RetryAfter(answer.Headers)) is { } failure
            ? throw failure
            : answer;

    /// <summary>
    /// One attempt, signed afresh. A request that got no answer throws <see cref="MfxTransportException"/>,
    /// or <see cref="MfxTimeoutException"/> when the client's timeout ran out; a cancellation the
    /// caller asked for stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    internal async Task<MfxAnswer> SendAsync(MfxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pathWithQuery = _basePath + Relative(request.Path) + Query(request.Query);
        // The URL is sent exactly as it is signed: canonicalising it would let the two disagree
        // about escaping, which the platform answers with a 401 nobody can explain.
        var url = new Uri(
            _origin + pathWithQuery,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        var body = request.Body ?? ReadOnlyMemory<byte>.Empty;
        using var message = new HttpRequestMessage(request.Method, url);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.UserAgent.ParseAdd(UserAgent);
        foreach (var (name, value) in Signer.HeadersFor(request.Method.Method, pathWithQuery, body.Span))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }
        if (request.IdempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        }
        if (request.Body is not null)
        {
            message.Content = new ReadOnlyMemoryContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        var what = $"{request.Method.Method} {Relative(request.Path)}";
        var started = _clock.GetTimestamp();
        int? status = null;
        try
        {
            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            NoteBudget(response.Headers);
            NoteAgreement(response.Headers);
            return new MfxAnswer((int)response.StatusCode, response.Headers, bytes);
        }
        catch (OperationCanceledException timedOut) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MfxTimeoutException($"{what} got no answer within {_http.Timeout.TotalSeconds:0.#} s", timedOut);
        }
        catch (HttpRequestException failed)
        {
            // Only a connection that never opened is known not to have sent anything.
            var neverSent = failed.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError;
            throw new MfxTransportException($"{what} got no answer: {failed.Message}", !neverSent, failed);
        }
        finally
        {
            Timed(new CallTiming(what, status, _clock.GetElapsedTime(started)));
        }
    }

    /// <summary>What the key had left when the platform last answered; null before the first answer.</summary>
    public Budget? Budget => _budget;

    /// <summary>
    /// Warns once per client, the first time any answer carries <c>MFX-Service-Agreement: due</c>, and never
    /// again on this client: the first sight is claimed atomically, so concurrent calls still warn once.
    /// </summary>
    private void NoteAgreement(HttpResponseHeaders headers)
    {
        if (Volatile.Read(ref _agreementWarned) == 0
            && headers.TryGetValues("MFX-Service-Agreement", out var values)
            && values.Any(v => string.Equals(v.Trim(), "due", StringComparison.OrdinalIgnoreCase))
            && Interlocked.Exchange(ref _agreementWarned, 1) == 0)
        {
#pragma warning disable CA1031 // a logger's bug must not fail the call that succeeded
            try
            {
                AgreementDue(_logger, null);
            }
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }
    }

    private void NoteBudget(HttpResponseHeaders headers)
    {
        // A header that is not a number tells us nothing; the last reading stands.
        if (headers.TryGetValues("X-MFX-Budget-Remaining", out var remaining)
            && long.TryParse(remaining.FirstOrDefault(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var left))
        {
            var source = headers.TryGetValues("X-MFX-Budget-Source", out var sources) ? sources.FirstOrDefault() ?? string.Empty : string.Empty;
            _budget = new Budget(left, source);
        }
    }

    /// <summary>
    /// Hands one attempt's timing on. Never throws: timing is an observation, not a reason for a call
    /// that succeeded to fail. The file gets the status and the time alone: no path, id, header or key.
    /// </summary>
    private void Timed(CallTiming timing)
    {
#pragma warning disable CA1031 // a listener's bug, or a log file that cannot be written, must not fail the call
        try
        {
            _onCall?.Invoke(timing);
        }
        catch (Exception)
        {
        }
        if (_callLog is not null)
        {
            try
            {
                var line = $$"""{"status":{{(timing.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}},"ms":{{Math.Round(timing.Elapsed.TotalMilliseconds, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""";
                File.AppendAllText(_callLog, line + "\n");
            }
            catch (Exception)
            {
            }
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Opens the multiplexed stream, signed as a request is, over <c>GET</c> on the stream's path.
    /// Close it with <see cref="MfxStream.CloseAsync"/>, or <c>await using</c>.
    /// </summary>
    public async Task<MfxStream> StreamAsync(StreamOptions? options = null, CancellationToken cancellationToken = default)
    {
        var stream = new MfxStream(this, options);
        await stream.OpenAsync(cancellationToken).ConfigureAwait(false);
        return stream;
    }

    /// <summary>The client's clock: signatures, deadlines and the waits of the polling calls are read from it.</summary>
    internal TimeProvider Clock => _clock;

    /// <summary>Waits as the client waits between attempts.</summary>
    internal Task PauseAsync(TimeSpan wait, CancellationToken cancellationToken) => _pause(wait, cancellationToken);

    /// <summary>
    /// Jittered exponential backoff for an attempt when the platform gave no delay: up to 1 s, 2 s,
    /// 4 s and so on, never more than 30 s.
    /// </summary>
    internal TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(1000 * Math.Pow(2, attempt), BackoffCap.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Round(_random() * ceiling));
    }

    /// <summary>
    /// The platform's <c>Retry-After</c> in seconds, never below zero; null when it is absent or not a
    /// number of seconds. The API writes whole seconds only, so an HTTP date is not read.
    /// </summary>
    internal static TimeSpan? RetryAfter(HttpResponseHeaders headers) =>
        headers.TryGetValues("Retry-After", out var values)
            && double.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            ? TimeSpan.FromSeconds(Math.Max(0, seconds))
            : null;

    private static string Relative(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static string Query(IReadOnlyList<KeyValuePair<string, string>>? query)
    {
        if (query is null || query.Count == 0)
        {
            return string.Empty;
        }
        var text = new StringBuilder("?");
        foreach (var (name, value) in query)
        {
            if (text.Length > 1)
            {
                text.Append('&');
            }
            text.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return text.ToString();
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    /// <inheritdoc/>
    public override string ToString() => $"MfxClient({_base}, KeyId={KeyId}, key=***)";
}

/// <summary>One request as the client sends it: the path is below the base URL.</summary>
internal sealed record MfxRequest(
    HttpMethod Method,
    string Path,
    IReadOnlyList<KeyValuePair<string, string>>? Query = null,
    ReadOnlyMemory<byte>? Body = null,
    string? IdempotencyKey = null);

/// <summary>What one attempt got back.</summary>
internal sealed record MfxAnswer(int Status, HttpResponseHeaders Headers, byte[] Body);

/// <summary>The SDK's version, as its <c>User-Agent</c> names it.</summary>
internal static class SdkVersion
{
    public const string Value = "0.1.0";
}
