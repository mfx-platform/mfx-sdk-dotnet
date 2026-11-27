using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// A fake MFX Hub API for the domain clients' tests: it answers each request with the next answer in
/// its queue and records what was sent, so a test reads the method, path, query, headers and body.
/// </summary>
internal sealed class FakeApi : HttpMessageHandler
{
    public const string Id1 = "0191f0c2-aaaa-7bbb-8ccc-000000000001";
    public const string Id2 = "0191f0c2-aaaa-7bbb-8ccc-000000000002";
    public const string Id3 = "0191f0c2-aaaa-7bbb-8ccc-000000000003";

    private readonly Queue<(int Status, string Body, string? RetryAfter, Exception? Error, (string Name, string Value)[] Headers)> _answers = new();

    public sealed record Sent(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        /// <summary>The body as JSON; null when there was none.</summary>
        public JsonObject? Json => Body.Length == 0 ? null : JsonNode.Parse(Body)!.AsObject();

        /// <summary>The idempotency key sent, or null.</summary>
        public string? Key => Headers.TryGetValue("Idempotency-Key", out var key) ? key : null;
    }

    public List<Sent> Requests { get; } = [];

    /// <summary>The clock the client reads, which every pause moves on.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));

    /// <summary>The pauses the client took, in order.</summary>
    public List<TimeSpan> Pauses { get; } = [];

    public FakeApi Answer(string body, int status = 200, string? retryAfter = null)
    {
        _answers.Enqueue((status, body, retryAfter, null, []));
        return this;
    }

    /// <summary>Answers the next request with <paramref name="body"/> and these response headers.</summary>
    public FakeApi AnswerWith(string body, int status, params (string Name, string Value)[] headers)
    {
        _answers.Enqueue((status, body, null, null, headers));
        return this;
    }

    /// <summary>Answers the next request by failing it as the HTTP stack would: no answer at all.</summary>
    public FakeApi Throw(Exception error)
    {
        _answers.Enqueue((0, string.Empty, null, error, []));
        return this;
    }

    public FakeApi Fail(int status, string code, string message = "refused", string details = "{}") =>
        Answer("{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\",\"details\":" + details + "}}", status);

    public static string PageOf(string? cursor, params string[] items) =>
        "{\"items\": [" + string.Join(",", items) + "], \"next_cursor\": "
        + (cursor is null ? "null" : "\"" + cursor + "\"") + ", \"returned\": " + items.Length + "}";

    public MfxClient Client(Microsoft.Extensions.Logging.ILogger? logger = null) =>
        new(new Uri("https://api.example.test/v1"), "mfxk_1", SigningKey.FromSeed(SigningTests.TestSeed),
            new MfxClientOptions
            {
                Handler = this,
                Clock = Clock,
                Logger = logger,
                Pause = (wait, _) =>
                {
                    Pauses.Add(wait);
                    Clock.Advance(wait);
                    return Task.CompletedTask;
                },
            });

    /// <summary>The one request sent; fails when there were more or none.</summary>
    public Sent Only => Assert.Single(Requests);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Sent(request.Method.Method, request.RequestUri!.PathAndQuery, headers, body));
        if (_answers.Count == 0)
        {
            throw new InvalidOperationException($"no answer queued for {request.Method} {request.RequestUri}");
        }
        var (status, text, retryAfter, error, extra) = _answers.Dequeue();
        if (error is not null)
        {
            throw error;
        }
        var answer = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
        {
            answer.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }
        foreach (var (name, value) in extra)
        {
            answer.Headers.TryAddWithoutValidation(name, value);
        }
        return answer;
    }

    /// <summary>A JSON text with @Id1, @Id2 and @Id3 standing for the ids, for bodies whose braces an interpolated raw string cannot hold.</summary>
    public static string R(string text) =>
        text.Replace("@Id1", Id1, StringComparison.Ordinal).Replace("@Id2", Id2, StringComparison.Ordinal).Replace("@Id3", Id3, StringComparison.Ordinal);

    /// <summary>A terminal as the API writes one.</summary>
    public static string Terminal(string id = Id1, string state = "ACTIVE", string extra = "") =>
        $$"""
        {"id": "{{id}}", "farm": {"id": "{{id}}"}, "broker": {"id": "{{id}}"}, "server": {"id": "{{id}}"},
         "streaming": false, "execution": true, "state": "{{state}}", "instruments": [], "created_at": "2026-10-23T09:00:00Z"{{extra}}}
        """;
}
