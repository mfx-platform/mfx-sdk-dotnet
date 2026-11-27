using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// Every failure a call can raise is typed by the platform's <c>error.code</c>, then by the HTTP
/// status, exactly as the shared error table (spec/surface.json) maps them.
/// </summary>
public sealed class ErrorsTests
{
    /// <summary>The reference's error table, less the errors the idiom map holds as pending (Enriched).</summary>
    private static readonly JsonArray Reference = new(
        [.. JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface.json")))!["errors"]!.AsArray()
            .Where(e => !Surface.IsPending("errors." + e!["error"]!.GetValue<string>()))
            .Select(e => e!.DeepClone())]);

    /// <summary>An error name in spec/surface.json as this SDK's (<see cref="Surface.ErrorName"/>).</summary>
    private static string Dotnet(string python) => Surface.ErrorName(python);

    private static MfxAnswer Answer(int status, string body, string? retryAfter = null)
    {
        using var message = new HttpResponseMessage((HttpStatusCode)status);
        if (retryAfter is not null)
        {
            message.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }
        return new MfxAnswer(status, message.Headers, Encoding.UTF8.GetBytes(body));
    }

    /// <summary>
    /// Given the shared error table (spec/surface.json) / When this SDK's mapping is read / Then
    /// every code and every status maps to the same error, by its .NET name, and there is no code or
    /// status the table does not map.
    /// </summary>
    [Fact]
    public void Every_code_and_status_maps_to_the_error_the_reference_sdk_names()
    {
        var byCode = Reference.Where(e => e!["code"] is not null)
            .ToDictionary(e => e!["code"]!.GetValue<string>(), e => Dotnet(e!["error"]!.GetValue<string>()));
        var byStatus = Reference.Where(e => e!["status"] is not null)
            .ToDictionary(e => e!["status"]!.GetValue<int>(), e => Dotnet(e!["error"]!.GetValue<string>()));

        Assert.NotEmpty(byCode);
        Assert.Equal(byCode.OrderBy(p => p.Key), ApiErrors.ByCodeTypes.ToDictionary(p => p.Key, p => p.Value.Name).OrderBy(p => p.Key));
        Assert.Equal(byStatus.OrderBy(p => p.Key), ApiErrors.ByStatusTypes.ToDictionary(p => p.Key, p => p.Value.Name).OrderBy(p => p.Key));
        Assert.All(ApiErrors.ByCodeTypes, p => Assert.Equal(p.Value, ApiErrors.For(p.Key, "m", 499).GetType()));
    }

    /// <summary>
    /// Given failed answers with a known code, an unknown code, no code, and no JSON at all / When
    /// each is read / Then the code decides, the status decides only when the code is unknown,
    /// neither gives the base error, a missing code reads as the error's name, a missing message is
    /// the call that failed, the details are kept read-only, and Retry-After is carried.
    /// </summary>
    [Fact]
    public void A_failed_answer_is_typed_by_its_code_then_its_status()
    {
        var upstream = ApiErrors.For(Answer(503,
            """{"error":{"code":"upstream_error","message":"the terminal saw no tick within 4 s","details":{"reason":"no_tick","age_s":4}}}"""),
            "GET /terminals/t-1", TimeSpan.FromSeconds(3));
        var details = Assert.IsType<UpstreamException>(upstream).Details;
        Assert.Equal("the terminal saw no tick within 4 s", upstream.Message);
        Assert.Equal("upstream_error (503): the terminal saw no tick within 4 s", upstream.ToString());
        Assert.Equal("no_tick", details["reason"].GetString());
        Assert.Equal(4, details["age_s"].GetInt32());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, JsonElement>)details).Clear());
        Assert.Equal(TimeSpan.FromSeconds(3), upstream.RetryAfter);

        var later = ApiErrors.For(Answer(409, """{"error":{"code":"some_code_from_later","message":"new"}}"""), "POST /x", null);
        Assert.IsType<ConflictException>(later);
        Assert.Equal("some_code_from_later", later.Code);

        var unknown = ApiErrors.For(Answer(418, """{"error":{"code":"teapot","message":"short and stout"}}"""), "GET /x", null);
        Assert.Equal(typeof(MfxApiException), unknown!.GetType());

        var edge = ApiErrors.For(Answer(502, "<html>bad gateway</html>"), "GET /terminals", null);
        Assert.IsType<EdgeException>(edge);
        Assert.IsAssignableFrom<UpstreamException>(edge);
        Assert.Equal("EdgeException", edge.Code);
        Assert.Equal("GET /terminals failed", edge.Message);
        Assert.Empty(edge.Details);

        Assert.Null(ApiErrors.For(Answer(200, "{}"), "GET /x", null));
        Assert.Null(ApiErrors.For(Answer(304, ""), "GET /x", null));
    }

    /// <summary>
    /// Given the errors whose details carry more (a suspended tenant, a maintenance window, a broker
    /// that is not supported, a risk refusal) / When their details are read / Then each reads its own
    /// fields, and a field that is absent or of the wrong kind is null, never an exception.
    /// </summary>
    [Fact]
    public void An_error_reads_the_fields_its_details_carry()
    {
        static MfxApiException Read(string code, string details) => ApiErrors.For(
            Answer(403, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"m\",\"details\":" + details + "}}"), "GET /x", null)!;

        var suspended = Assert.IsType<TenantSuspendedException>(Read("tenant_suspended",
            """{"since":"2026-11-15T08:00:00Z","reads_until":"2026-11-16T08:00:00+04:00"}"""));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero), suspended.Since);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 4, 0, 0, TimeSpan.Zero), suspended.ReadsUntil);
        Assert.Null(Assert.IsType<TenantDisabledException>(Read("tenant_disabled", """{"since":42}""")).Since);

        var window = Assert.IsType<MaintenanceException>(Read("maintenance",
            """{"window_id":"w-1","window":"MW-0031","ends_at":"2026-11-15T02:38:00Z","service":"order_placement","suspended":["order_placement",7,"md"]}"""));
        Assert.Equal(("w-1", "MW-0031", "order_placement"), (window.WindowId, window.Window, window.Service));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 2, 38, 0, TimeSpan.Zero), window.EndsAt);
        Assert.Equal(["order_placement", "md"], window.Suspended);
        Assert.Empty(Assert.IsType<MaintenanceException>(Read("maintenance", "{}")).Suspended);

        var broker = Assert.IsType<UnsupportedBrokerException>(Read("broker_not_supported",
            """{"refused_by":"server_disabled","reason":"r","broker_id":"b-1","server_id":"s-1"}"""));
        Assert.Equal(("server_disabled", "r", "b-1", "s-1"), (broker.RefusedBy, broker.Reason, broker.BrokerId, broker.ServerId));

        var risk = Assert.IsType<RiskRefusedException>(Read("risk_refused",
            """{"rule":"max_daily_loss","policy_id":"p-1","version":"3","used":"412.50","limit":500,"would_be":"","challenge_version":true}"""));
        Assert.Equal(new RiskRefusal("risk_refused", "max_daily_loss", "p-1", 3, null, null, "412.50", "500", null), risk.Refusal);
        Assert.IsAssignableFrom<ValidationException>(risk);
    }

    /// <summary>
    /// Given a request whose connection never opens, and one that gets no answer before the client's
    /// timeout / When each is sent / Then the first is a transport error that cannot have reached the
    /// platform, the second a timeout that may have, and a cancellation the caller asked for stays a
    /// cancellation.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_answer_says_whether_it_could_have_arrived()
    {
        static MfxClient Over(HttpMessageHandler handler, TimeSpan timeout) => new(
            new Uri("https://api.example.test/v1"), "mfxk_1", SigningKey.FromSeed(SigningTests.TestSeed),
            new MfxClientOptions { Handler = handler, Timeout = timeout });

        using (var refused = Over(new Throwing(new HttpRequestException(HttpRequestError.ConnectionError, "refused")), TimeSpan.FromSeconds(5)))
        {
            var failure = await Assert.ThrowsAsync<MfxTransportException>(
                () => refused.ReadAsync(new MfxRequest(HttpMethod.Get, "/terminals"), CancellationToken.None));
            Assert.False(failure.MayHaveReached);
            Assert.Contains("GET /terminals", failure.Message, StringComparison.Ordinal);
        }
        using (var dropped = Over(new Throwing(new HttpRequestException(HttpRequestError.ResponseEnded, "ended")), TimeSpan.FromSeconds(5)))
        {
            var failure = await Assert.ThrowsAsync<MfxTransportException>(
                () => dropped.WriteAsync(new MfxRequest(HttpMethod.Post, "/terminals", Body: "{}"u8.ToArray()), CancellationToken.None));
            Assert.True(failure.MayHaveReached);
        }
        using (var slow = Over(new Hanging(), TimeSpan.FromMilliseconds(50)))
        {
            var timeout = await Assert.ThrowsAsync<MfxTimeoutException>(
                () => slow.ReadAsync(new MfxRequest(HttpMethod.Get, "/terminals"), CancellationToken.None));
            Assert.True(timeout.MayHaveReached);
            using var stop = new CancellationTokenSource();
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => slow.ReadAsync(new MfxRequest(HttpMethod.Get, "/terminals"), stop.Token));
        }
    }

    /// <summary>A read rate limited past its retries, and a write refused, throw their typed errors.</summary>
    [Fact]
    public async Task A_failed_read_or_write_throws_its_typed_error()
    {
        using var handler = new Answering(
            [(HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limited","message":"slow down"}}""", "7")]);
        using var client = new MfxClient(new Uri("https://api.example.test/v1"), "mfxk_1",
            SigningKey.FromSeed(SigningTests.TestSeed), new MfxClientOptions { Handler = handler, ReadRetries = 0 });

        var limited = await Assert.ThrowsAsync<RateLimitException>(
            () => client.ReadAsync(new MfxRequest(HttpMethod.Get, "/terminals"), CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(7), limited.RetryAfter);
        Assert.Equal(429, limited.Status);
    }

    private sealed class Throwing(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Answering((HttpStatusCode Status, string Body, string? RetryAfter)[] answers) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body, retryAfter) = answers[_next++];
            var answer = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is not null)
            {
                answer.Headers.RetryAfter = RetryConditionHeaderValue.Parse(retryAfter);
            }
            return Task.FromResult(answer);
        }
    }
}
