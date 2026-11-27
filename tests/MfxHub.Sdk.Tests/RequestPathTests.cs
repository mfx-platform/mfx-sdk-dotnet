using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The client's request path: every attempt is signed afresh over exactly what is sent, a read
/// that is rate limited is tried again as the platform says, and a write is sent once.
/// </summary>
public sealed class RequestPathTests
{
    private static readonly Uri Base = new("https://api.example.test/v1");

    private sealed record Sent(HttpMethod Method, Uri Url, Dictionary<string, string> Headers, byte[] Body);

    private sealed class Recording(params HttpResponseMessage[] answers) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _answers = new(answers);

        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
            }
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new Sent(request.Method, request.RequestUri!, headers, body));
            return _answers.Dequeue();
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body = "{}") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RateLimited(string? retryAfter)
    {
        var answer = Answer(HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limited","message":"slow down"}}""");
        if (retryAfter is not null)
        {
            answer.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }
        return answer;
    }

    private static (MfxClient Client, List<TimeSpan> Waits) Client(
        Recording handler, int readRetries = MfxClient.DefaultReadRetries, FakeTimeProvider? clock = null)
    {
        var waits = new List<TimeSpan>();
        var client = new MfxClient(Base, "mfxk_C0NF0RMANCE1", SigningKey.FromSeed(SigningTests.TestSeed), new MfxClientOptions
        {
            Handler = handler,
            ReadRetries = readRetries,
            Clock = clock,
            Random = () => 0.5,
            Pause = (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
        });
        return (client, waits);
    }

    private static void AssertSignedOver(Sent sent, string pathWithQuery)
    {
        var message = Signing.Message(
            sent.Headers[Signing.TimestampHeader], sent.Headers[Signing.NonceHeader],
            sent.Method.Method, pathWithQuery, sent.Body);
        Assert.Equal("mfxk_C0NF0RMANCE1", sent.Headers[Signing.KeyHeader]);
        Assert.True(
            SigningTests.Verifies(SigningKey.FromSeed(SigningTests.TestSeed).PublicKey(), message, sent.Headers[Signing.SignatureHeader]),
            "the signature verifies over what was sent");
    }

    /// <summary>
    /// Given a read with a query and a write with a body to a path with a colon in it / When each is
    /// sent / Then each carries the key id, a timestamp, a nonce and a signature that verifies over
    /// its method, its path with the base path and the query exactly as sent, and its body; the
    /// write carries its idempotency key and its JSON content type; and the colon stays in the path.
    /// </summary>
    [Fact]
    public async Task Every_request_is_signed_over_exactly_what_is_sent()
    {
        var handler = new Recording(Answer(HttpStatusCode.OK), Answer(HttpStatusCode.Accepted));
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000));
        var (client, _) = Client(handler, clock: clock);
        using var _ = client;

        await client.ReadAsync(new MfxRequest(HttpMethod.Get, "/terminals",
            [new("limit", "2"), new("cursor", "c 2/+")]), CancellationToken.None);
        var body = Encoding.UTF8.GetBytes("""{"reason":"nightly","farm_id":"f-1"}""");
        await client.WriteAsync(new MfxRequest(HttpMethod.Post, "terminals:restart", Body: body,
            IdempotencyKey: "bulk-1"), CancellationToken.None);

        var read = handler.Requests[0];
        Assert.Equal("/v1/terminals?limit=2&cursor=c%202%2F%2B", read.Url.PathAndQuery);
        Assert.Equal("1790000000000", read.Headers[Signing.TimestampHeader]);
        Assert.Equal(MfxClient.UserAgent, read.Headers["User-Agent"]);
        Assert.Equal("application/json", read.Headers["Accept"]);
        AssertSignedOver(read, "/v1/terminals?limit=2&cursor=c%202%2F%2B");
        Assert.Empty(read.Body);

        var write = handler.Requests[1];
        Assert.Equal("https://api.example.test/v1/terminals:restart", write.Url.OriginalString);
        Assert.Equal(body, write.Body);
        Assert.Equal("bulk-1", write.Headers["Idempotency-Key"]);
        Assert.StartsWith("application/json", write.Headers["Content-Type"], StringComparison.Ordinal);
        AssertSignedOver(write, "/v1/terminals:restart");
        Assert.NotEqual(read.Headers[Signing.NonceHeader], write.Headers[Signing.NonceHeader]);
    }

    /// <summary>
    /// Given a read answered 429 with Retry-After in seconds, then as an HTTP date, then with none /
    /// When the client tries again / Then it waits the seconds the platform said, or a jittered
    /// backoff when told nothing it reads (the API writes whole seconds only, so a date is not read),
    /// and each attempt is signed again with a new nonce; after its retries
    /// are spent the 429 is a RateLimitException carrying the last wait.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_read_is_tried_again_as_the_platform_says_and_signed_again()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero));
        var handler = new Recording(
            RateLimited("2"),
            RateLimited("Sun, 15 Nov 2026 08:00:05 GMT"),
            RateLimited(null),
            RateLimited("1"));
        var (client, waits) = Client(handler, clock: clock);
        using var _ = client;

        var limited = await Assert.ThrowsAsync<RateLimitException>(
            () => client.ReadAsync(new MfxRequest(HttpMethod.Get, "/terminals/t-1"), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(1), limited.RetryAfter);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2000)], waits);
        Assert.Equal(4, handler.Requests.Select(r => r.Headers[Signing.NonceHeader]).Distinct().Count());
        Assert.All(handler.Requests, sent => AssertSignedOver(sent, "/v1/terminals/t-1"));

        var dated = new Recording(RateLimited("Sun, 15 Nov 2026 08:00:05 GMT"), RateLimited("-3"));
        var (late, lateWaits) = Client(dated, readRetries: 1, clock: clock);
        using var __ = late;
        var told = await Assert.ThrowsAsync<RateLimitException>(
            () => late.ReadAsync(new MfxRequest(HttpMethod.Get, "/terminals"), CancellationToken.None));
        Assert.Equal([TimeSpan.FromMilliseconds(500)], lateWaits);
        Assert.Equal(TimeSpan.Zero, told.RetryAfter);
    }

    /// <summary>
    /// Given a write answered 429 / When it is sent / Then it is sent once and the 429 is a
    /// RateLimitException: a retried write is a second order.
    /// </summary>
    [Fact]
    public async Task A_write_is_never_tried_again()
    {
        var handler = new Recording(RateLimited("0"));
        var (client, waits) = Client(handler);
        using var _ = client;

        await Assert.ThrowsAsync<RateLimitException>(() => client.WriteAsync(new MfxRequest(HttpMethod.Post, "/terminals",
            Body: Encoding.UTF8.GetBytes("{}"), IdempotencyKey: "k-1"), CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Empty(waits);
    }

    /// <summary>
    /// Given writes with a key, with a blank key and with none / When each is sent / Then the key goes
    /// as given, a blank one is refused before anything is sent, and none is ever invented: a key
    /// made up by the client would make the caller's own retry a second order.
    /// </summary>
    [Fact]
    public async Task An_idempotency_key_is_the_callers_and_never_invented()
    {
        var handler = new Recording(Answer(HttpStatusCode.Accepted), Answer(HttpStatusCode.OK));
        var (client, _) = Client(handler);
        using var _ = client;
        var body = Encoding.UTF8.GetBytes("{}");

        await client.WriteAsync(new MfxRequest(HttpMethod.Post, "/terminals", Body: body, IdempotencyKey: "mine-1"), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteAsync(
            new MfxRequest(HttpMethod.Post, "/terminals", Body: body, IdempotencyKey: "  "), CancellationToken.None));
        await client.WriteAsync(new MfxRequest(HttpMethod.Post, "/terminals/t-1/confirm", Body: body), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("mine-1", handler.Requests[0].Headers["Idempotency-Key"]);
        Assert.False(handler.Requests[1].Headers.ContainsKey("Idempotency-Key"));
    }

    /// <summary>The backoff doubles from 1 s and never passes 30 s; a relative or odd base is refused.</summary>
    [Fact]
    public void The_backoff_doubles_to_a_cap_and_the_base_url_is_checked()
    {
        var (client, _) = Client(new Recording());
        using var _ = client;

        Assert.Equal(TimeSpan.FromMilliseconds(500), client.Backoff(0));
        Assert.Equal(TimeSpan.FromMilliseconds(4000), client.Backoff(3));
        Assert.Equal(TimeSpan.FromMilliseconds(15000), client.Backoff(10));
        Assert.Equal(new Uri("https://api.example.test/v1"), client.BaseUrl);
        Assert.DoesNotContain("AAEC", client.ToString(), StringComparison.Ordinal);

        var key = SigningKey.FromSeed(SigningTests.TestSeed);
        Assert.Throws<ArgumentException>(() => new MfxClient(new Uri("/v1", UriKind.Relative), "k", key));
        Assert.Throws<ArgumentException>(() => new MfxClient(new Uri("ftp://api.example.test/v1"), "k", key));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MfxClient(Base, "k", key, new MfxClientOptions { ReadRetries = -1 }));
    }
}
