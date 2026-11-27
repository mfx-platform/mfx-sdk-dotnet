using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The terminal writes beyond Core (create, the credentials, restart, replacing the instruments, the
/// expert-advisor writes, the bulk restart, drain, resume and retire): each sends its body once, with the caller's key.
/// </summary>
public sealed class TerminalWritesTests
{
    private static readonly Guid One = Guid.Parse(FakeApi.Id1);
    private static readonly Guid Two = Guid.Parse(FakeApi.Id2);

    /// <summary>
    /// Given a broker, a server and the caller's key / When a terminal is asked for / Then one POST of
    /// /terminals carries the key as Idempotency-Key and the body in the contract's names, with the
    /// farm left out when none is named and redundancy none by default; a rate-limited answer is
    /// thrown and never sent again.
    /// </summary>
    [Fact]
    public async Task A_terminal_is_asked_for_once_with_the_callers_key()
    {
        using var api = new FakeApi().Answer(FakeApi.Terminal(state: "REQUESTED"), 201)
            .Answer("""{"error":{"code":"rate_limited","message":"slow down"}}""", 429, "0");
        using var client = api.Client();

        var terminal = await client.Terminals.CreateAsync(One, Two, "key-1", instruments: ["EURUSD.sd"],
            labels: new Dictionary<string, string> { ["desk"] = "fx" }, streaming: true);
        await Assert.ThrowsAsync<RateLimitException>(() => client.Terminals.CreateAsync(One, Two, "key-2"));

        Assert.Equal(TenantTerminalState.Requested, terminal.State);
        Assert.Equal(2, api.Requests.Count);
        var sent = api.Requests[0];
        Assert.Equal(("POST", "/v1/terminals", "key-1"), (sent.Method, sent.PathAndQuery, sent.Key));
        var body = sent.Json!;
        Assert.False(body.ContainsKey("farm_id"));
        Assert.Equal(FakeApi.Id1, body["broker_id"]!.GetValue<string>());
        Assert.Equal(FakeApi.Id2, body["server_id"]!.GetValue<string>());
        Assert.True(body["streaming"]!.GetValue<bool>());
        Assert.Equal("none", body["redundancy"]!.GetValue<string>());
        Assert.Equal("EURUSD.sd", body["instruments"]![0]!.GetValue<string>());
        Assert.Equal("fx", body["labels"]!["desk"]!.GetValue<string>());
        Assert.Equal("key-2", api.Requests[1].Key);
    }

    /// <summary>
    /// Given a blank or missing idempotency key / When any terminal write is called / Then it is
    /// refused before anything is sent: a key is the caller's own, never invented.
    /// </summary>
    [Fact]
    public async Task A_blank_key_is_refused_before_anything_is_sent()
    {
        using var api = new FakeApi();
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.Terminals.CreateAsync(One, Two, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Terminals.RetireAsync(One, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Terminals.ResumeAsync(One, null!));

        Assert.Empty(api.Requests);
    }

    /// <summary>
    /// Given a broker login and its password / When the credential is submitted / Then the password
    /// goes in the body once, as given, and the secret it travelled in prints as *** and refuses to be
    /// serialised; a login larger than an int is kept whole.
    /// </summary>
    [Fact]
    public async Task A_credential_is_sent_once_and_its_secret_never_prints()
    {
        using var api = new FakeApi().Answer(
            $$"""{"rotated": false, "takes_effect": "next_login", "credential_ref_id": "{{FakeApi.Id3}}", "terminal_id": "{{FakeApi.Id1}}", "cred_type": "investor", "execution_disabled": true}""");
        using var client = api.Client();
        Secret password = "hunter2";

        var accepted = await client.Terminals.SubmitCredentialsAsync(
            One, 5_000_000_000, password, "Broker-Demo", "cred-1", CredentialSubmissionCredType.Investor);

        Assert.True(accepted.ExecutionDisabled);
        var sent = api.Only;
        Assert.Equal(("POST", "/v1/terminals/" + FakeApi.Id1 + "/credentials", "cred-1"), (sent.Method, sent.PathAndQuery, sent.Key));
        Assert.Equal(5_000_000_000, sent.Json!["login"]!.GetValue<long>());
        Assert.Equal("hunter2", sent.Json["password"]!.GetValue<string>());
        Assert.Equal("investor", sent.Json["cred_type"]!.GetValue<string>());
        Assert.Equal("Secret(***)", password.ToString());
        Assert.Equal("Secret(***)", $"{password}");
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Serialize(password));
    }

    /// <summary>
    /// Given a terminal / When it is restarted, its instruments replaced, an expert attached, detached
    /// and confirmed, and it is drained, resumed and retired / Then each is one request on the contract's
    /// route: the restart queues by default, the detach goes as query parameters, the confirmation
    /// carries no key, and the others the caller's.
    /// </summary>
    [Fact]
    public async Task Each_terminal_write_is_one_request_on_its_route()
    {
        var accepted = $$"""{"operation_id": "{{FakeApi.Id2}}", "state": "ACCEPTED"}""";
        using var api = new FakeApi().Answer(accepted, 202).Answer(FakeApi.Terminal()).Answer(accepted, 202)
            .Answer(accepted, 202).Answer($$"""{"expert_advisor_id": "{{FakeApi.Id3}}"}""")
            .Answer(FakeApi.Terminal(state: "DRAINING")).Answer(FakeApi.Terminal(state: "REQUESTED")).Answer(FakeApi.Terminal(state: "DRAINING"));
        using var client = api.Client();
        var t = client.Terminals;

        await t.RestartAsync(One, "stuck on login", "k-restart");
        await t.ReplaceInstrumentsAsync(One, ["EURUSD", "XAUUSD"], "k-instruments", depthInstruments: ["XAUUSD"]);
        await t.AttachExpertAdvisorAsync(One, Guid.Parse(FakeApi.Id3), "EURUSD", ExpertAdvisorAttachTimeframe.H1, "trial", "k-attach");
        await t.DetachExpertAdvisorAsync(One, "done", "k-detach", confirmOpenPositions: true);
        await t.ConfirmExpertAdvisorAsync(One, "123456");
        await t.DrainAsync(One, "k-drain", reason: "weekend");
        await t.ResumeAsync(One, "k-resume");
        await t.RetireAsync(One, "k-retire");

        var path = "/v1/terminals/" + FakeApi.Id1;
        Assert.Equal(
            [
                ("POST", path + "/restart", "k-restart"),
                ("PUT", path + "/instruments", "k-instruments"),
                ("PUT", path + "/expert-advisor", "k-attach"),
                ("DELETE", path + "/expert-advisor?reason=done&queue=false&confirm_open_positions=true", "k-detach"),
                ("POST", path + "/expert-advisor/confirm", null),
                ("POST", path + "/drain", "k-drain"),
                ("POST", path + "/resume", "k-resume"),
                ("DELETE", path, "k-retire"),
            ],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        Assert.True(api.Requests[0].Json!["queue"]!.GetValue<bool>());
        Assert.False(api.Requests[0].Json!["confirm_open_positions"]!.GetValue<bool>());
        Assert.Equal("XAUUSD", api.Requests[1].Json!["depth_instruments"]![0]!.GetValue<string>());
        Assert.Equal("H1", api.Requests[2].Json!["timeframe"]!.GetValue<string>());
        Assert.Equal("123456", api.Requests[4].Json!["code"]!.GetValue<string>());
        Assert.Equal("weekend", api.Requests[5].Json!["reason"]!.GetValue<string>());
        Assert.Empty(api.Requests[6].Body);
    }

    /// <summary>
    /// Given a bulk restart / When it names no farm, broker or terminals, a blank or too long reason, or
    /// more than 1000 terminals / Then it is refused before anything is sent; a valid one is one POST
    /// of /terminals:restart, and a bulk action is read and cancelled by its id.
    /// </summary>
    [Fact]
    public async Task A_bulk_restart_is_checked_before_it_is_sent()
    {
        var action = $$"""{"id": "{{FakeApi.Id2}}", "kind": "restart", "state": "RUNNING", "requested_by": "key", "reason": "r", "requested_n": 1, "accepted_n": 1, "refused_n": 0, "refused": [], "counts": {}, "late": false, "created_at": "2026-11-17T09:00:00Z"}""";
        using var api = new FakeApi().Answer(action, 202).Answer(action).Answer(action);
        using var client = api.Client();
        var t = client.Terminals;

        await Assert.ThrowsAsync<ArgumentException>(() => t.BulkRestartAsync("why", "k"));
        await Assert.ThrowsAsync<ArgumentException>(() => t.BulkRestartAsync(" ", "k", farmId: One));
        await Assert.ThrowsAsync<ArgumentException>(() => t.BulkRestartAsync(new string('x', 501), "k", farmId: One));
        await Assert.ThrowsAsync<ArgumentException>(() => t.BulkRestartAsync("why", "k", terminalIds: Enumerable.Repeat(One, 1001)));
        Assert.Empty(api.Requests);

        await t.BulkRestartAsync("cell patch", "k-bulk", farmId: One, terminalIds: [Two]);
        await t.GetBulkActionAsync(Two);
        await t.CancelBulkActionAsync(Two, "k-cancel");

        Assert.Equal(
            [("POST", "/v1/terminals:restart", "k-bulk"), ("GET", "/v1/bulk-actions/" + FakeApi.Id2, null), ("POST", "/v1/bulk-actions/" + FakeApi.Id2 + "/cancel", "k-cancel")],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        var body = api.Requests[0].Json!;
        Assert.Equal(FakeApi.Id1, body["farm_id"]!.GetValue<string>());
        Assert.False(body.ContainsKey("broker_id"));
        Assert.Equal(FakeApi.Id2, body["terminal_ids"]![0]!.GetValue<string>());
    }
}
