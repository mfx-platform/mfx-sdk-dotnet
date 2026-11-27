using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>Checking a webhook delivery.</summary>
public sealed class WebhookVerifyTests
{
    private const string Body = """{"type":"position.closed","data":{"ticket":"7001"}}""";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_763_370_000);

    private static string Sign(string secret, long stamp, string body = Body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{stamp}.{body}")));

    /// <summary>
    /// Given a delivery signed with the endpoint's secret / When it is checked, and a delivery after a
    /// rotation is checked with the new and the previous secret / Then each answers its signed time: the
    /// delivery is genuine when any secret made any one of the header's v1 values.
    /// </summary>
    [Fact]
    public void A_delivery_signed_by_a_secret_given_is_genuine()
    {
        var stamp = Now.ToUnixTimeSeconds() - 10;

        var one = Webhooks.Verify(Body, $"t={stamp},v1={Sign("whsec_new", stamp)}", "whsec_new", now: Now);
        var rotated = Webhooks.Verify(Encoding.UTF8.GetBytes(Body), $"t={stamp}, v1=deadbeef, v1={Sign("whsec_old", stamp)}",
            [new Secret("whsec_new"), new Secret("whsec_old")], now: Now);

        Assert.Equal(stamp, one);
        Assert.Equal(stamp, rotated);
    }

    /// <summary>
    /// Given a header with no time, one with no v1, a delivery signed six minutes ago, and one signed by
    /// another secret or over another body / When each is checked / Then each is refused with its reason:
    /// malformed, stale, mismatch.
    /// </summary>
    [Fact]
    public void A_delivery_not_provably_from_the_hub_is_refused_with_its_reason()
    {
        var stamp = Now.ToUnixTimeSeconds();
        string Reason(System.Action check) => Assert.Throws<WebhookSignatureException>(check).Reason;

        Assert.Equal("malformed", Reason(() => Webhooks.Verify(Body, $"v1={Sign("s", stamp)}", "s", now: Now)));
        Assert.Equal("malformed", Reason(() => Webhooks.Verify(Body, $"t={stamp}", "s", now: Now)));
        Assert.Equal("stale", Reason(() => Webhooks.Verify(Body, $"t={stamp - 360},v1={Sign("s", stamp - 360)}", "s", now: Now)));
        Assert.Equal("mismatch", Reason(() => Webhooks.Verify(Body, $"t={stamp},v1={Sign("other", stamp)}", "s", now: Now)));
        Assert.Equal("mismatch", Reason(() => Webhooks.Verify(Body + " ", $"t={stamp},v1={Sign("s", stamp)}", "s", now: Now)));
        Assert.Equal(stamp - 360, Webhooks.Verify(Body, $"t={stamp - 360},v1={Sign("s", stamp - 360)}", "s", TimeSpan.FromMinutes(10), Now));
    }
}

/// <summary>Webhook endpoints.</summary>
public sealed class WebhookEndpointTests
{
    private static readonly Guid Endpoint = Guid.Parse(FakeApi.Id1);
    private static string Row => $$"""{"id": "{{FakeApi.Id1}}", "url": "https://hooks.example.test/mfx", "event_types": ["position.closed"]}""";

    /// <summary>
    /// Given an HTTPS address and event types / When an endpoint is registered / Then one POST of /webhooks
    /// carries them with the key and the description, the secret comes back, and an address that is not an
    /// absolute web address is refused before sending.
    /// </summary>
    [Fact]
    public async Task An_endpoint_is_registered_and_its_secret_comes_back_once()
    {
        using var api = new FakeApi().Answer($$"""{"id": "{{FakeApi.Id1}}", "url": "https://hooks.example.test/mfx", "event_types": ["*"], "secret": "whsec_1"}""", 201);
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.Webhooks.CreateAsync("hooks.example.test", ["*"], "k"));
        var created = await client.Webhooks.CreateAsync("https://hooks.example.test/mfx", ["position.closed", "terminal.*"], "hook-1", description: "desk");

        var sent = api.Only;
        Assert.Equal(("POST", "/v1/webhooks", "hook-1"), (sent.Method, sent.PathAndQuery, sent.Key));
        Assert.Equal("""{"url":"https://hooks.example.test/mfx","event_types":["position.closed","terminal.*"],"description":"desk"}""", sent.Body);
        Assert.Equal("whsec_1", JsonSerializer.SerializeToNode(created, MfxJson.Options)!["secret"]!.GetValue<string>());
    }

    /// <summary>
    /// Given an endpoint / When the endpoints are listed, it is read, paused, its secret rotated, a test
    /// asked for and it is deleted / Then each is one request on its route: the update sends only what was
    /// given (status paused), the rotation carries the key, and the test and the delete carry none.
    /// </summary>
    [Fact]
    public async Task Each_endpoint_call_is_one_request_and_an_update_sends_only_what_changes()
    {
        using var api = new FakeApi().Answer("{\"items\": [" + Row + "]}").Answer(Row).Answer(Row)
            .Answer($$"""{"secret": "whsec_2", "previous_secret_until": "2026-11-18T09:00:00Z"}""").Answer("""{"event_id": "e-1"}""", 202).Answer("", 204);
        using var client = api.Client();
        var w = client.Webhooks;

        var all = await w.ListAsync();
        await w.GetAsync(Endpoint);
        await w.UpdateAsync(Endpoint, "upd-1", paused: true);
        await w.RotateSecretAsync(Endpoint, "rot-1");
        await w.TestAsync(Endpoint);
        await w.DeleteAsync(Endpoint);

        Assert.Single(all);
        var p = "/v1/webhooks/" + FakeApi.Id1;
        Assert.Equal(
            [("GET", "/v1/webhooks", null), ("GET", p, null), ("PATCH", p, "upd-1"), ("POST", p + "/rotate-secret", "rot-1"), ("POST", p + "/test", null), ("DELETE", p, null)],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        Assert.Equal("""{"status":"paused"}""", api.Requests[2].Body);
    }
}

/// <summary>What did not arrive, and sending it again.</summary>
public sealed class WebhookFailuresAndReplayTests
{
    private static readonly Guid Endpoint = Guid.Parse(FakeApi.Id1);

    /// <summary>
    /// Given failures on two pages / When they are walked narrowed to retrying and dead / Then the states go
    /// as one comma-separated value on every page and the cursor is followed to its end.
    /// </summary>
    [Fact]
    public async Task Failures_follow_the_cursor_narrowed_by_state()
    {
        var row = $$"""{"id": "{{FakeApi.Id2}}", "state": "dead", "attempts": []}""";
        using var api = new FakeApi().Answer(FakeApi.PageOf("c-2", row)).Answer(FakeApi.PageOf(null, row));
        using var client = api.Client();

        var states = new List<WebhookFailureState>();
        await foreach (var failure in client.Webhooks.FailuresAsync(Endpoint, [WebhookFailureState.Retrying, WebhookFailureState.Dead], limit: 1))
        {
            states.Add(failure.State);
        }

        Assert.Equal([WebhookFailureState.Dead, WebhookFailureState.Dead], states);
        var p = $"/v1/webhooks/{FakeApi.Id1}/failures";
        Assert.Equal([p + "?state=retrying%2Cdead&limit=1", p + "?state=retrying%2Cdead&cursor=c-2&limit=1"], api.Requests.Select(r => r.PathAndQuery));
    }

    /// <summary>
    /// Given an endpoint / When events are replayed after a stream cursor, and one failure is replayed /
    /// Then each is one POST with its key and only the field given; naming both or neither is refused
    /// before anything is sent.
    /// </summary>
    [Fact]
    public async Task A_replay_names_exactly_one_of_a_cursor_and_a_failure()
    {
        using var api = new FakeApi().Answer("""{"replay_id": "r-1"}""", 202).Answer("""{"replay_id": "r-2"}""", 202);
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.Webhooks.ReplayAsync(Endpoint, "k"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Webhooks.ReplayAsync(Endpoint, "k", after: "e-1", failureId: Endpoint));
        await client.Webhooks.ReplayAsync(Endpoint, "rep-1", after: "1763370000000-0");
        await client.Webhooks.ReplayAsync(Endpoint, "rep-2", failureId: Guid.Parse(FakeApi.Id2));

        Assert.Equal(["""{"after":"1763370000000-0"}""", $$"""{"failure_id":"{{FakeApi.Id2}}"}"""], api.Requests.Select(r => r.Body));
        Assert.Equal(["rep-1", "rep-2"], api.Requests.Select(r => r.Key));
    }
}

/// <summary>Expert Advisor versions.</summary>
public sealed class ExpertAdvisorTests
{
    private static readonly Guid Ea = Guid.Parse(FakeApi.Id1);

    private static string Version(string state) =>
        $$"""{"id": "{{FakeApi.Id1}}", "name": "Breakout", "version": 1, "state": "{{state}}", "findings": {}, "analysers": [], "source_sha256": "ab", "size_bytes": 10, "file_count": 1, "terminals": [], "created_at": "2026-11-17T09:00:00Z"}""";

    /// <summary>
    /// Given a bundle of one .mq5 and one .mqh / When it is uploaded / Then one POST of /expert-advisors
    /// carries the name and each file's path with its source as UTF-8 in base64, with the key; and a
    /// version is read, paged, reported on and withdrawn on its routes.
    /// </summary>
    [Fact]
    public async Task A_bundle_is_uploaded_as_base64_text_and_a_version_read_and_withdrawn()
    {
        using var api = new FakeApi().Answer(Version("uploaded"), 202).Answer(Version("approved")).Answer(FakeApi.PageOf(null, Version("approved")))
            .Answer("""{"findings": []}""").Answer(Version("withdrawn"));
        using var client = api.Client();
        var files = new Dictionary<string, string> { ["Breakout.mq5"] = "#include \"lib.mqh\"\nvoid OnTick() {}", ["lib.mqh"] = "// é" };

        var uploaded = await client.ExpertAdvisors.UploadAsync("Breakout", files, "ea-1");
        await client.ExpertAdvisors.GetAsync(Ea);
        await client.ExpertAdvisors.PageAsync(limit: 5);
        await client.ExpertAdvisors.ReportAsync(Ea);
        var withdrawn = await client.ExpertAdvisors.DeleteAsync(Ea, "ea-del-1");

        Assert.Equal((TenantExpertAdvisorState.Uploaded, TenantExpertAdvisorState.Withdrawn), (uploaded.State, withdrawn.State));
        var body = api.Requests[0].Json!;
        Assert.Equal("Breakout", body["name"]!.GetValue<string>());
        Assert.Equal("lib.mqh", body["files"]![1]!["path"]!.GetValue<string>());
        Assert.Equal("// é", Encoding.UTF8.GetString(Convert.FromBase64String(body["files"]![1]!["content_base64"]!.GetValue<string>())));
        var p = "/v1/expert-advisors";
        Assert.Equal([("POST", p, "ea-1"), ("GET", p + "/" + FakeApi.Id1, null), ("GET", p + "?limit=5", null), ("GET", p + "/" + FakeApi.Id1 + "/report", null), ("DELETE", p + "/" + FakeApi.Id1, "ea-del-1")],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
    }

    /// <summary>
    /// Given a version being analysed, then judged, then approved / When its verdict is waited for / Then it
    /// is read after 2 s and 4 s and the approved version returned; one still analysing when the time runs
    /// out is returned as it is.
    /// </summary>
    [Fact]
    public async Task A_verdict_is_waited_for_until_the_version_is_settled()
    {
        using var api = new FakeApi().Answer(Version("analysing")).Answer(Version("judged")).Answer(Version("approved"))
            .Answer(Version("analysing")).Answer(Version("analysing"));
        using var client = api.Client();

        var settled = await client.ExpertAdvisors.WaitForVerdictAsync(Ea);
        var running = await client.ExpertAdvisors.WaitForVerdictAsync(Ea, TimeSpan.FromSeconds(1));

        Assert.Equal(TenantExpertAdvisorState.Approved, settled.State);
        Assert.Equal(TenantExpertAdvisorState.Analysing, running.State);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)], api.Pauses);
    }
}

/// <summary>An expert's telemetry and the tenant's contacts.</summary>
public sealed class TelemetryAndTenantTests
{
    private static StreamMessage Message(string? type, string data) =>
        new(Channel.Ea(Guid.Parse(FakeApi.Id1)), 4, type, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data)!);

    /// <summary>
    /// Given a telemetry batch, a telemetry-lost message and another message on an ea: channel / When each
    /// is read / Then the batch's records keep their segment, line and record in order with its counts,
    /// the loss names its segments and records, and the other is null.
    /// </summary>
    [Fact]
    public void Telemetry_reads_as_a_batch_or_a_loss()
    {
        var batch = Assert.IsType<EaTelemetry>(ExpertAdvisors.TelemetryOf(Message(ExpertAdvisors.Telemetry,
            """{"records": [{"segment": 3, "line": 9, "record": {"signal": "buy"}}, {"segment": 3, "line": 10, "record": {"signal": "sell"}}], "skipped": 1}""")));
        var lost = Assert.IsType<EaTelemetryLost>(ExpertAdvisors.TelemetryOf(Message(ExpertAdvisors.TelemetryLost,
            """{"segment_from": 1, "segment_to": 2, "records": 400}""")));

        Assert.Equal([(3L, 9L), (3L, 10L)], batch.Records.Select(r => (r.Segment, r.Line)));
        Assert.Equal("sell", batch.Records[1].Record["signal"].GetString());
        Assert.Equal((1L, 0L, 4L), (batch.Skipped, batch.Dropped, batch.Seq!.Value));
        Assert.Equal((1L, 2L, 400L), (lost.SegmentFrom, lost.SegmentTo, lost.Records));
        Assert.Null(ExpertAdvisors.TelemetryOf(Message("ea.something", "{}")));
    }

    /// <summary>Given the tenant / When its contacts are read / Then one GET of /tenant/contacts answers them.</summary>
    [Fact]
    public async Task The_tenants_contacts_are_one_read()
    {
        using var api = new FakeApi().Answer("""{"contacts": []}""");
        using var client = api.Client();

        await client.Tenant.ContactsAsync();

        Assert.Equal(("GET", "/v1/tenant/contacts"), (api.Only.Method, api.Only.PathAndQuery));
    }
}
