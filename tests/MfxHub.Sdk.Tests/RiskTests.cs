using System.Text.Json;
using System.Text.Json.Nodes;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>Risk Management's policies.</summary>
public sealed class RiskPolicyTests
{
    private static readonly Guid Policy = Guid.Parse(FakeApi.Id1);
    private static readonly string[] Notify = ["notify"];

    private static string PolicyRow(string state = "active") =>
        $$"""{"id": "{{FakeApi.Id1}}", "name": "Funded", "state": "{{state}}", "current_version": {"version": 1}, "accounts": [], "created_at": "2026-11-17T09:00:00Z"}""";

    /// <summary>
    /// Given rules as an anonymous object holding decimals, and a reset / When a policy is created / Then
    /// one POST of /risk/policies carries the name, rules and reset with the key, each decimal as exact
    /// text; a rules mapping holding a double is refused before anything is sent.
    /// </summary>
    [Fact]
    public async Task A_policy_is_created_with_exact_figures_and_a_double_is_refused()
    {
        using var api = new FakeApi().Answer(PolicyRow(), 201);
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.Risk.CreatePolicyAsync(
            "Funded", new { daily_loss = new { limit = new { pct = 5.5 } } }, new { zone = "broker" }, "k"));
        Assert.Empty(api.Requests);
        var policy = await client.Risk.CreatePolicyAsync(
            "Funded", new { daily_loss = new { limit = new { pct = 5.50m }, check_entries = true, actions = new { kinds = Notify } } },
            new { zone = "broker" }, "policy-1");

        Assert.Equal(Policy, policy.Id);
        var sent = api.Only;
        Assert.Equal(("POST", "/v1/risk/policies", "policy-1"), (sent.Method, sent.PathAndQuery, sent.Key));
        Assert.Equal("""{"name":"Funded","rules":{"daily_loss":{"limit":{"pct":"5.50"},"check_entries":true,"actions":{"kinds":["notify"]}}},"reset":{"zone":"broker"}}""", sent.Body);
    }

    /// <summary>
    /// Given a policy / When a version is added with a reason, it is archived, and activated on two accounts /
    /// Then each is one POST on its route with the key: the reason in the version's body, the archive with
    /// no body, the activation naming the accounts and the version.
    /// </summary>
    [Fact]
    public async Task A_policy_is_versioned_archived_and_activated()
    {
        using var api = new FakeApi().Answer($$"""{"policy_id": "{{FakeApi.Id1}}", "version": 2}""", 201).Answer(PolicyRow("archived"))
            .Answer("""{"results": []}""");
        using var client = api.Client();

        await client.Risk.AddPolicyVersionAsync(Policy, new JsonObject { ["exposure"] = new JsonObject { ["max_lots"] = "2" } },
            new JsonObject { ["zone"] = "utc" }, "v-2", reason: "loosen exposure");
        var archived = await client.Risk.ArchivePolicyAsync(Policy, "arch-1");
        await client.Risk.ActivateAsync(Policy, [Guid.Parse(FakeApi.Id2)], 2, "act-1");

        var p = "/v1/risk/policies/" + FakeApi.Id1;
        Assert.Equal([(p + "/versions", "v-2"), (p + "/archive", "arch-1"), (p + "/activate", "act-1")], api.Requests.Select(r => (r.PathAndQuery, r.Key)));
        Assert.Equal("loosen exposure", api.Requests[0].Json!["reason"]!.GetValue<string>());
        Assert.Empty(api.Requests[1].Body);
        Assert.Equal($$"""{"account_ids":["{{FakeApi.Id2}}"],"version":2}""", api.Requests[2].Body);
        Assert.Equal(RiskPolicyState.Archived, archived.State);
    }

    /// <summary>
    /// Given policies on two pages / When they are listed active and oldest first, and a policy's versions
    /// are walked / Then the state and the sort go on every page, newest is sent when no sort is named,
    /// and each walk follows the cursor.
    /// </summary>
    [Fact]
    public async Task Policies_and_their_versions_follow_the_cursor_with_their_sort()
    {
        using var api = new FakeApi().Answer(FakeApi.PageOf("c-2", PolicyRow())).Answer(FakeApi.PageOf(null, PolicyRow()))
            .Answer(FakeApi.PageOf(null, $$"""{"policy_id": "{{FakeApi.Id1}}", "version": 1}"""));
        using var client = api.Client();

        var listed = 0;
        await foreach (var _ in client.Risk.PoliciesAsync(RiskPolicyState.Active, TimeSort.Oldest, limit: 1))
        {
            listed++;
        }
        await foreach (var _ in client.Risk.PolicyVersionsAsync(Policy))
        {
            listed++;
        }

        Assert.Equal(3, listed);
        Assert.Equal(
            ["/v1/risk/policies?limit=1&state=active&sort=oldest", "/v1/risk/policies?cursor=c-2&limit=1&state=active&sort=oldest", $"/v1/risk/policies/{FakeApi.Id1}/versions"],
            api.Requests.Select(r => r.PathAndQuery));
    }
}

/// <summary>Risk Management's protected accounts.</summary>
public sealed class RiskAccountsTests
{
    private static readonly Guid Account = Guid.Parse(FakeApi.Id1);
    private static string AccountRow => $$"""{"account_id": "{{FakeApi.Id1}}"}""";

    /// <summary>
    /// Given protected accounts / When a page is read by two coverages, a restriction and a sort, and every
    /// account is walked with none / Then several values go as repeated parameters, the sort is sent always
    /// (daily_loss_used by default), the page keeps its stats, and a value the enum does not define is refused.
    /// </summary>
    [Fact]
    public async Task Accounts_are_filtered_by_repeated_parameters_and_keep_their_stats()
    {
        using var api = new FakeApi().Answer("{\"stats\": {\"accounts\": 7}, \"items\": [" + AccountRow + "], \"next_cursor\": null}")
            .Answer(FakeApi.PageOf("c-2", AccountRow)).Answer(FakeApi.PageOf(null, AccountRow));
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Risk.AccountsPageAsync(coverage: [(CoverageHealth)50]));
        var page = await client.Risk.AccountsPageAsync(
            coverage: [CoverageHealth.Healthy, CoverageHealth.Degraded], restriction: [RestrictionState.EntriesBlocked], sort: RiskAccountSort.Plans);
        var all = 0;
        await foreach (var _ in client.Risk.AccountsAsync())
        {
            all++;
        }

        Assert.Equal(7, JsonSerializer.SerializeToNode(page.Stats, MfxJson.Options)!["accounts"]!.GetValue<int>());
        Assert.Equal(2, all);
        Assert.Equal(
            ["/v1/risk/accounts?coverage=healthy&coverage=degraded&restriction=entries_blocked&sort=plans",
             "/v1/risk/accounts?sort=daily_loss_used", "/v1/risk/accounts?cursor=c-2&sort=daily_loss_used"],
            api.Requests.Select(r => r.PathAndQuery));
    }

    /// <summary>
    /// Given a protected account / When it is read, its equity and close scope read, it is cleared and
    /// deactivated, and the coverage read / Then each is one request on its route: the equity's window as
    /// from and to, the scope as the contract's word, and the writes with their reason and key.
    /// </summary>
    [Fact]
    public async Task Each_account_call_is_one_request_on_its_route()
    {
        using var api = new FakeApi().Answer(AccountRow).Answer("{}").Answer("{}").Answer(AccountRow).Answer(AccountRow).Answer("{}");
        using var client = api.Client();
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        await client.Risk.AccountAsync(Account);
        await client.Risk.EquityAsync(Account, start);
        await client.Risk.CloseScopeAsync(Account, RiskCloseScopeScope.MfxPositions);
        await client.Risk.ClearAsync(Account, RiskClearRule.DailyLoss, "reviewed", "clear-1");
        await client.Risk.DeactivateAsync(Account, "closed", "deact-1");
        await client.Risk.CoverageAsync();

        var p = "/v1/risk/accounts/" + FakeApi.Id1;
        Assert.Equal(
            [("GET", p, null), ("GET", p + "/equity?from=2026-11-01T00%3A00%3A00%2B00%3A00", null), ("GET", p + "/close-scope?scope=mfx_positions", null),
             ("POST", p + "/clear", "clear-1"), ("POST", p + "/deactivate", "deact-1"), ("GET", "/v1/risk/coverage", null)],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        Assert.Equal("""{"rule":"daily_loss","reason":"reviewed"}""", api.Requests[3].Body);
        Assert.Equal("""{"reason":"closed"}""", api.Requests[4].Body);
    }
}

/// <summary>The planner and exit plans.</summary>
public sealed class RiskPlannerAndPlansTests
{
    private static readonly Guid Account = Guid.Parse(FakeApi.Id1);
    private static string Plan(string state = "armed") => $$"""{"id": "{{FakeApi.Id2}}", "account_id": "{{FakeApi.Id1}}", "state": "{{state}}"}""";

    /// <summary>
    /// Given an order already sized / When it is checked, and a preview is asked for while the platform is
    /// rate limited once / Then each is a POST with no key, the check's order goes as given with its volume
    /// as text, the preview is tried again after the platform's Retry-After as a read is, and a double in
    /// the order is refused before anything is sent.
    /// </summary>
    [Fact]
    public async Task The_planner_is_read_without_a_key_and_its_figures_go_as_text()
    {
        using var api = new FakeApi().Answer("""{"verdict": "allow", "checks": []}""")
            .Answer("""{"error":{"code":"rate_limited","message":"slow"}}""", 429, "2").Answer("{}");
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.Risk.CheckAsync(Account, new Dictionary<string, object> { ["volume"] = 0.4 }));
        var check = await client.Risk.CheckAsync(Account, new Dictionary<string, object> { ["symbol"] = "EURUSD", ["side"] = "buy", ["type"] = "market", ["volume"] = 0.40m });
        await client.Risk.PreviewAsync(new JsonObject { ["account_id"] = FakeApi.Id1, ["symbol"] = "EURUSD", ["sl"] = "1.0950" });

        Assert.Equal(RiskCheckResultVerdict.Allow, check.Verdict);
        Assert.Equal(
            [("POST", "/v1/risk/planner/check", null), ("POST", "/v1/risk/planner/preview", null), ("POST", "/v1/risk/planner/preview", null)],
            api.Requests.Select(r => (r.Method, r.PathAndQuery, r.Key)));
        Assert.Equal("""{"account_id":"@ID","order":{"symbol":"EURUSD","side":"buy","type":"market","volume":"0.40"}}""".Replace("@ID", FakeApi.Id1, StringComparison.Ordinal), api.Requests[0].Body);
        Assert.Equal([TimeSpan.FromSeconds(2)], api.Pauses);
    }

    /// <summary>
    /// Given a plan / When it is created, read, amended, paused, resumed and ended, and plans are listed by
    /// two accounts and a state / Then each write carries its key on its route (the end a DELETE), the
    /// amend its steps and reason, and the list its repeated filters with its sort.
    /// </summary>
    [Fact]
    public async Task A_plan_is_placed_amended_paused_resumed_and_ended()
    {
        using var api = new FakeApi().Answer(Plan(), 201).Answer(Plan()).Answer(Plan()).Answer(Plan("paused")).Answer(Plan()).Answer(Plan("ended"))
            .Answer("{\"items\": [" + Plan() + "], \"next_cursor\": null}");
        using var client = api.Client();
        var r = client.Risk;
        var planId = Guid.Parse(FakeApi.Id2);
        var step = new { trigger = new { kind = "r", value = "1" }, action = new { kind = "close_part", pct = "50" }, held_by = "mfx" };

        await r.CreatePlanAsync(new { account_id = FakeApi.Id1, ticket = "7001", steps = new[] { step } }, "plan-1");
        await r.PlanAsync(planId);
        await r.AmendPlanAsync(planId, [step], "amend-1", reason: "tighter");
        await r.PausePlanAsync(planId, "news", "pause-1");
        await r.ResumePlanAsync(planId, "resume-1");
        var ended = await r.EndPlanAsync(planId, "end-1");
        await r.PlansPageAsync(accountId: [Account, planId], state: [RiskPlanState.Armed, RiskPlanState.Managing], sort: TimeSort.Oldest);

        Assert.Equal(RiskPlanState.Ended, ended.State);
        var p = "/v1/risk/plans/" + FakeApi.Id2;
        Assert.Equal(
            [("POST", "/v1/risk/plans", "plan-1"), ("GET", p, null), ("POST", p + "/amend", "amend-1"), ("POST", p + "/pause", "pause-1"),
             ("POST", p + "/resume", "resume-1"), ("DELETE", p, "end-1"),
             ("GET", $"/v1/risk/plans?account_id={FakeApi.Id1}&account_id={FakeApi.Id2}&state=armed&state=managing&sort=oldest", null)],
            api.Requests.Select(x => (x.Method, x.PathAndQuery, x.Key)));
        Assert.Equal("tighter", api.Requests[2].Json!["reason"]!.GetValue<string>());
        Assert.Equal("close_part", api.Requests[2].Json!["steps"]![0]!["action"]!["kind"]!.GetValue<string>());
    }
}

/// <summary>Challenges.</summary>
public sealed class RiskChallengeTests
{
    private static readonly Guid Challenge = Guid.Parse(FakeApi.Id1);
    private static string ChallengeRow(string state = "active") => $$"""{"id": "{{FakeApi.Id1}}", "name": "Phase 1", "state": "{{state}}"}""";

    /// <summary>
    /// Given a challenge's rules / When it is created, versioned, archived and two accounts enrolled / Then
    /// each is a POST with its key: the create carrying the name and rules, the version the rules alone,
    /// the archive no body, the enrolment the account ids; an archive refused because an account is in
    /// progress is a StateException naming them.
    /// </summary>
    [Fact]
    public async Task A_challenge_is_created_versioned_archived_and_enrolled()
    {
        using var api = new FakeApi().Answer(ChallengeRow(), 201).Answer(ChallengeRow(), 201).Answer(ChallengeRow("archived"))
            .Answer("""{"results": []}""").Fail(409, "state_error", "accounts in progress", $$"""{"accounts": ["{{FakeApi.Id3}}"]}""");
        using var client = api.Client();
        var rules = new JsonObject { ["min_trading_days"] = 5, ["clock"] = "broker", ["profit_target"] = new JsonObject { ["pct"] = "8" } };

        await client.Risk.CreateChallengeAsync("Phase 1", rules, "ch-1");
        await client.Risk.AddChallengeVersionAsync(Challenge, rules, "ch-2");
        var archived = await client.Risk.ArchiveChallengeAsync(Challenge, "ch-3");
        await client.Risk.EnrolAsync(Challenge, [Guid.Parse(FakeApi.Id2), Guid.Parse(FakeApi.Id3)], "ch-4");
        var refused = await Assert.ThrowsAsync<StateException>(() => client.Risk.ArchiveChallengeAsync(Challenge, "ch-5"));

        Assert.Equal(RiskChallengeState.Archived, archived.State);
        var p = "/v1/risk/challenges";
        Assert.Equal([(p, "ch-1"), (p + "/" + FakeApi.Id1 + "/versions", "ch-2"), (p + "/" + FakeApi.Id1 + "/archive", "ch-3"),
                      (p + "/" + FakeApi.Id1 + "/accounts", "ch-4"), (p + "/" + FakeApi.Id1 + "/archive", "ch-5")],
            api.Requests.Select(r => (r.PathAndQuery, r.Key)));
        Assert.Equal("""{"name":"Phase 1","rules":{"min_trading_days":5,"clock":"broker","profit_target":{"pct":"8"}}}""", api.Requests[0].Body);
        Assert.Equal("""{"min_trading_days":5,"clock":"broker","profit_target":{"pct":"8"}}""", api.Requests[1].Body);
        Assert.Equal(FakeApi.Id3, refused.Details["accounts"][0].GetString());
    }

    /// <summary>
    /// Given a challenge's accounts on two pages / When they are walked by two states, a rule, below the
    /// minimum days and a sort, and one is read with its periods / Then every page carries the repeated
    /// filters and the sort, below_min_days goes as true, and the account is its own route.
    /// </summary>
    [Fact]
    public async Task A_challenges_accounts_follow_their_filters_and_sort()
    {
        var row = $$"""{"account_id": "{{FakeApi.Id2}}", "version": 1, "state": "in_progress"}""";
        using var api = new FakeApi().Answer(FakeApi.PageOf("k-2", row)).Answer(FakeApi.PageOf(null, row)).Answer("{}");
        using var client = api.Client();

        var ids = new List<Guid>();
        await foreach (var account in client.Risk.ChallengeAccountsAsync(Challenge,
            state: [RiskChallengeAccountState.InProgress, RiskChallengeAccountState.Passed], rule: [RiskChallengeRule.DailyLoss],
            belowMinDays: true, sort: RiskChallengeSort.Profit))
        {
            ids.Add(account.AccountId);
        }
        await client.Risk.ChallengeAccountAsync(Challenge, Guid.Parse(FakeApi.Id2));

        Assert.Equal(2, ids.Count);
        var p = $"/v1/risk/challenges/{FakeApi.Id1}/accounts";
        Assert.Equal(
            [p + "?state=in_progress&state=passed&rule=daily_loss&below_min_days=true&sort=profit",
             p + "?cursor=k-2&state=in_progress&state=passed&rule=daily_loss&below_min_days=true&sort=profit", p + "/" + FakeApi.Id2],
            api.Requests.Select(r => r.PathAndQuery));
    }
}

/// <summary>A refused entry and the risk events.</summary>
public sealed class RiskRefusalAndEventsTests
{
    private static StreamMessage Message(string type, string data) =>
        new("events", 1, type, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data)!);

    /// <summary>
    /// Given an exit plan whose order breaks the daily loss / When it is placed / Then a RiskRefusedException
    /// (a ValidationException, code risk_refused) is thrown, its refusal naming the reason, rule, policy and
    /// version, and the figures as the platform wrote them; activity is read unresolved first by default.
    /// </summary>
    [Fact]
    public async Task A_refused_entry_names_its_rule_and_figures()
    {
        using var api = new FakeApi().Fail(422, "risk_refused", "the daily loss would be exceeded",
            $$"""{"reason": "daily_loss_reached", "rule": "daily_loss", "policy_id": "{{FakeApi.Id3}}", "version": 3, "used": "240.00", "limit": "300.00", "would_be": "310.40"}""")
            .Answer(FakeApi.PageOf(null));
        using var client = api.Client();

        var refused = await Assert.ThrowsAsync<RiskRefusedException>(() => client.Risk.CreatePlanAsync(
            new { account_id = FakeApi.Id1, order = new { symbol = "EURUSD", side = "buy", type = "market", volume = 0.40m } }, "plan-x"));
        await foreach (var _ in client.Risk.ActivityAsync(kind: [RiskActivityKind.Breach], start: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)))
        {
        }

        Assert.IsAssignableFrom<ValidationException>(refused);
        Assert.Equal(("risk_refused", 422), (refused.Code, refused.Status));
        var why = refused.Refusal;
        Assert.Equal(("daily_loss_reached", "daily_loss", FakeApi.Id3, 3, "240.00", "300.00", "310.40"),
            (why.Reason, why.Rule, why.PolicyId, why.Version, why.Used, why.Limit, why.WouldBe));
        Assert.Equal("/v1/risk/activity?kind=breach&from=2026-11-01T00%3A00%3A00%2B00%3A00&sort=unresolved_first", api.Requests[1].PathAndQuery);
    }

    /// <summary>
    /// Given the four risk events as the stream writes them / When each is read / Then a rule event keeps
    /// which of the three it is, its blank ids, version and figures read as null, its actions are a list
    /// from the JSON text; a coverage change names the kind of coverage; a programme result and a closed
    /// period read whole.
    /// </summary>
    [Fact]
    public void The_risk_events_read_as_their_types()
    {
        var rule = Assert.IsType<RiskRuleEvent>(MfxEvent.Of(Message(RiskRuleEvent.Breached, $$"""
            {"schema_version": "1", "account_id": "{{FakeApi.Id1}}", "rule": "daily_loss", "policy_id": "{{FakeApi.Id2}}", "challenge_id": "",
             "version": "3", "used": "300.00", "limit": "300.00", "level": "act", "actions": "[\"notify\",\"block_entries\"]", "cleared_by": "", "at": "2026-11-17T10:00:00Z"}
            """)));
        var coverage = Assert.IsType<RiskCoverageChanged>(MfxEvent.Of(Message(RiskCoverageChanged.Type,
            $$"""{"schema_version": "1", "account_id": "{{FakeApi.Id1}}", "kind": "pre_trade", "from": "healthy", "to": "degraded", "reason": "", "at": "2026-11-17T10:00:00Z"}""")));
        var result = Assert.IsType<RiskProgrammeResult>(MfxEvent.Of(Message(RiskProgrammeResult.Type,
            $$"""{"schema_version": "1", "challenge_id": "{{FakeApi.Id2}}", "version": "1", "account_id": "{{FakeApi.Id1}}", "result": "failed", "rule": "max_drawdown", "at": "2026-11-17T10:00:00Z"}""")));
        var period = Assert.IsType<RiskPeriodClosed>(MfxEvent.Of(Message(RiskPeriodClosed.Type,
            $$"""{"schema_version": "1", "challenge_id": "{{FakeApi.Id2}}", "account_id": "{{FakeApi.Id1}}", "period": "{\"kind\":\"daily\",\"pnl\":\"-12.50\"}", "at": "2026-11-17T10:00:00Z"}""")));

        Assert.Equal((RiskRuleEvent.Breached, (Guid?)Guid.Parse(FakeApi.Id2), (Guid?)null, (int?)3, (decimal?)300.00m, (string?)null),
            (rule.Kind, rule.PolicyId, rule.ChallengeId, rule.Version, rule.Used, rule.ClearedBy));
        Assert.Equal(["notify", "block_entries"], rule.Actions);
        Assert.Equal(("pre_trade", "healthy", "degraded", (string?)null), (coverage.Coverage, coverage.FromState, coverage.ToState, coverage.Reason));
        Assert.Equal(("failed", "max_drawdown", 1), (result.Result, result.Rule, result.Version));
        Assert.Equal("-12.50", period.Period["pnl"].GetString());
    }
}
