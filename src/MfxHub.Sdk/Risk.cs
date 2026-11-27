using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MfxHub.Models;

namespace MfxHub;

/// <summary>An account's coverage, as Risk Management's list filters it.</summary>
public enum CoverageHealth
{
    /// <summary>Every kind of coverage is healthy.</summary>
    [JsonStringEnumMemberName("healthy")] Healthy,

    /// <summary>A kind of coverage is degraded.</summary>
    [JsonStringEnumMemberName("degraded")] Degraded,

    /// <summary>A kind of coverage is disconnected.</summary>
    [JsonStringEnumMemberName("disconnected")] Disconnected,

    /// <summary>Coverage is not known.</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,

    /// <summary>The account holds no rules.</summary>
    [JsonStringEnumMemberName("no_rules")] NoRules,
}

/// <summary>How Risk Management's account list is sorted.</summary>
public enum RiskAccountSort
{
    /// <summary>By daily loss used, most first (the default).</summary>
    [JsonStringEnumMemberName("daily_loss_used")] DailyLossUsed,

    /// <summary>By drawdown used.</summary>
    [JsonStringEnumMemberName("drawdown_used")] DrawdownUsed,

    /// <summary>By exposure.</summary>
    [JsonStringEnumMemberName("exposure")] Exposure,

    /// <summary>By the age of the inputs.</summary>
    [JsonStringEnumMemberName("inputs_age")] InputsAge,

    /// <summary>By plans.</summary>
    [JsonStringEnumMemberName("plans")] Plans,
}

/// <summary>Newest or oldest first.</summary>
public enum TimeSort
{
    /// <summary>Newest first (the default).</summary>
    [JsonStringEnumMemberName("newest")] Newest,

    /// <summary>Oldest first.</summary>
    [JsonStringEnumMemberName("oldest")] Oldest,
}

/// <summary>How Risk Management's activity is sorted.</summary>
public enum RiskActivitySort
{
    /// <summary>Unresolved first (the default).</summary>
    [JsonStringEnumMemberName("unresolved_first")] UnresolvedFirst,

    /// <summary>Newest first.</summary>
    [JsonStringEnumMemberName("newest")] Newest,

    /// <summary>Oldest first.</summary>
    [JsonStringEnumMemberName("oldest")] Oldest,
}

/// <summary>A challenge rule an account can fail on.</summary>
public enum RiskChallengeRule
{
    /// <summary>The daily loss.</summary>
    [JsonStringEnumMemberName("daily_loss")] DailyLoss,

    /// <summary>The maximum drawdown.</summary>
    [JsonStringEnumMemberName("max_drawdown")] MaxDrawdown,
}

/// <summary>How a challenge's accounts are sorted.</summary>
public enum RiskChallengeSort
{
    /// <summary>By daily loss used (the default).</summary>
    [JsonStringEnumMemberName("daily_loss_used")] DailyLossUsed,

    /// <summary>By drawdown.</summary>
    [JsonStringEnumMemberName("drawdown")] Drawdown,

    /// <summary>By profit.</summary>
    [JsonStringEnumMemberName("profit")] Profit,

    /// <summary>By trading days.</summary>
    [JsonStringEnumMemberName("trading_days")] TradingDays,

    /// <summary>By the end of the current period.</summary>
    [JsonStringEnumMemberName("period_ends_at")] PeriodEndsAt,
}

/// <summary>
/// Risk Management (<c>client.Risk</c>): account rules, the trade planner, exit plans and challenges.
/// The rules, thresholds and actions are the tenant's; MFX Hub runs them on the account's own numbers
/// and says what it did and why.
/// </summary>
/// <remarks>
/// Bodies (rules, resets, steps, orders, a preview) are given as the generated models or as anything
/// that serialises to the contract's shape (a <see cref="JsonObject"/>, a dictionary, an anonymous
/// object); a decimal goes as exact text, and a binary floating-point number anywhere in one is refused
/// before anything is sent. A new entry that breaks a rule in force is refused with
/// <see cref="RiskRefusedException"/>, whose <see cref="RiskRefusedException.Refusal"/> names the rule,
/// the figure used, its limit and what the entry would have made it. Several values of a filter go as
/// repeated parameters.
/// </remarks>
public sealed class Risk
{
    private readonly MfxClient _client;

    internal Risk(MfxClient client) => _client = client;

    private static string Id(Guid id) => Wire.Id(id);

    private static JsonObject Body(params (string Name, JsonNode? Value)[] fields)
    {
        var body = new JsonObject();
        foreach (var (name, value) in fields)
        {
            if (value is not null)
            {
                body[name] = value;
            }
        }
        return body;
    }

    private static JsonNode Required(object? value, string parameter) =>
        Wire.Setting(value, parameter) ?? throw new ArgumentNullException(parameter);

    private static JsonArray Ids(IEnumerable<Guid> ids, string parameter)
    {
        ArgumentNullException.ThrowIfNull(ids, parameter);
        return [.. ids.Select(i => (JsonNode)JsonValue.Create(Wire.Id(i)))];
    }

    private Task<T> Post<T>(string path, JsonObject? body, string idempotencyKey, CancellationToken cancellationToken) =>
        _client.WriteAsync<T>(HttpMethod.Post, path, body, Keys.Required(idempotencyKey), cancellationToken);

    // --- policies ---------------------------------------------------------------------------

    /// <summary>Creates a versioned rule set: daily loss, drawdown, exposure, loss at stops, a schedule.</summary>
    /// <param name="name">The policy's name.</param>
    /// <param name="rules">The rules, in the contract's shape.</param>
    /// <param name="reset">When the daily figures reset.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The policy, at version 1.</returns>
    public Task<RiskPolicy> CreatePolicyAsync(string name, object rules, object reset, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var body = Body(("name", name), ("rules", Required(rules, nameof(rules))), ("reset", Required(reset, nameof(reset))));
        return Post<RiskPolicy>("/risk/policies", body, idempotencyKey, cancellationToken);
    }

    /// <summary>
    /// Adds a version to a policy. A version that relaxes protection needs <c>risk:override</c> and a
    /// <paramref name="reason"/> (<see cref="RiskOverrideRequiredException"/> otherwise).
    /// </summary>
    /// <param name="policyId">The policy.</param>
    /// <param name="rules">The new rules.</param>
    /// <param name="reset">When the daily figures reset.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="reason">Why, for a relaxing version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new version.</returns>
    public Task<RiskPolicyVersion> AddPolicyVersionAsync(
        Guid policyId, object rules, object reset, string idempotencyKey, string? reason = null, CancellationToken cancellationToken = default)
    {
        var body = Body(("rules", Required(rules, nameof(rules))), ("reset", Required(reset, nameof(reset))), ("reason", reason));
        return Post<RiskPolicyVersion>($"/risk/policies/{Id(policyId)}/versions", body, idempotencyKey, cancellationToken);
    }

    /// <summary>
    /// Archives a policy: it can no longer be activated or versioned. Refused with
    /// <see cref="StateException"/> while any account holds it (<c>details.accounts</c> names them);
    /// archiving it again answers it unchanged.
    /// </summary>
    /// <param name="policyId">The policy.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The policy, with its <c>archived_at</c>.</returns>
    public Task<RiskPolicy> ArchivePolicyAsync(Guid policyId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskPolicy>($"/risk/policies/{Id(policyId)}/archive", null, idempotencyKey, cancellationToken);

    /// <summary>Puts a version of a policy in force on accounts: one outcome per account.</summary>
    /// <param name="policyId">The policy.</param>
    /// <param name="accountIds">The accounts.</param>
    /// <param name="version">The version to put in force.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One outcome per account.</returns>
    public Task<RiskActivationResult> ActivateAsync(
        Guid policyId, IEnumerable<Guid> accountIds, int version, string idempotencyKey, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var body = Body(("account_ids", Ids(accountIds, nameof(accountIds))), ("version", version), ("reason", reason));
        return Post<RiskActivationResult>($"/risk/policies/{Id(policyId)}/activate", body, idempotencyKey, cancellationToken);
    }

    /// <summary>One policy.</summary>
    /// <param name="policyId">The policy.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The policy.</returns>
    public Task<RiskPolicy> PolicyAsync(Guid policyId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskPolicy>($"/risk/policies/{Id(policyId)}", null, cancellationToken);

    /// <summary>One version of a policy.</summary>
    /// <param name="policyId">The policy.</param>
    /// <param name="version">The version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version.</returns>
    public Task<RiskPolicyVersion> PolicyVersionAsync(Guid policyId, int version, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskPolicyVersion>(
            $"/risk/policies/{Id(policyId)}/versions/{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null, cancellationToken);

    /// <summary>Every policy, following the cursor, newest or oldest first, active or archived.</summary>
    /// <param name="state">Only active or only archived policies; every one when null.</param>
    /// <param name="sort">Newest first by default.</param>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every policy.</returns>
    public IAsyncEnumerable<RiskPolicy> PoliciesAsync(
        RiskPolicyState? state = null, TimeSort sort = TimeSort.Newest, int? limit = null, CancellationToken cancellationToken = default)
    {
        var kept = state is { } s ? Wire.Word(s) : null;
        var order = Wire.Word(sort);
        return Pages.All((cursor, token) => _client.PageAsync<RiskPolicy>("/risk/policies",
            new Query().Add("cursor", cursor).Add("limit", limit).Add("state", kept).Add("sort", order), token), cancellationToken);
    }

    /// <summary>Every version of a policy, following the cursor.</summary>
    /// <param name="policyId">The policy.</param>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every version.</returns>
    public IAsyncEnumerable<RiskPolicyVersion> PolicyVersionsAsync(Guid policyId, int? limit = null, CancellationToken cancellationToken = default) =>
        Pages.All((cursor, token) => _client.PageAsync<RiskPolicyVersion>($"/risk/policies/{Id(policyId)}/versions",
            new Query().Add("cursor", cursor).Add("limit", limit), token), cancellationToken);

    // --- accounts ---------------------------------------------------------------------------

    private static Query AccountsQuery(IEnumerable<CoverageHealth>? coverage, IEnumerable<RestrictionState>? restriction, RiskAccountSort sort) =>
        new Query().Repeated("coverage", Wire.Words(coverage)).Repeated("restriction", Wire.Words(restriction)).Add("sort", Wire.Word(sort));

    /// <summary>One page of the protected accounts and its <c>stats</c>, filtered and sorted by a figure.</summary>
    /// <param name="coverage">Only accounts with these coverages.</param>
    /// <param name="restriction">Only accounts with these restrictions.</param>
    /// <param name="sort">Daily loss used by default.</param>
    /// <param name="cursor">The cursor of the page before.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, with its stats.</returns>
    public Task<RiskAccountPage> AccountsPageAsync(
        IEnumerable<CoverageHealth>? coverage = null, IEnumerable<RestrictionState>? restriction = null,
        RiskAccountSort sort = RiskAccountSort.DailyLossUsed, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskAccountPage>("/risk/accounts",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(AccountsQuery(coverage, restriction, sort)), cancellationToken);

    /// <summary>Every protected account, following the cursor, filtered and sorted as <see cref="AccountsPageAsync"/>.</summary>
    /// <param name="coverage">As <see cref="AccountsPageAsync"/>.</param>
    /// <param name="restriction">As <see cref="AccountsPageAsync"/>.</param>
    /// <param name="sort">As <see cref="AccountsPageAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every account.</returns>
    public IAsyncEnumerable<RiskAccount> AccountsAsync(
        IEnumerable<CoverageHealth>? coverage = null, IEnumerable<RestrictionState>? restriction = null,
        RiskAccountSort sort = RiskAccountSort.DailyLossUsed, CancellationToken cancellationToken = default)
    {
        var filters = AccountsQuery(coverage, restriction, sort);
        return Pages.All((cursor, token) => _client.PageAsync<RiskAccount>("/risk/accounts",
            new Query().Add("cursor", cursor).AddRange(filters), token), cancellationToken);
    }

    /// <summary>One protected account: its figures, coverage and restriction.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account.</returns>
    public Task<RiskAccount> AccountAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskAccount>($"/risk/accounts/{Id(accountId)}", null, cancellationToken);

    /// <summary>An account's equity over a window.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="start">The window's start.</param>
    /// <param name="end">The window's end; now when null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The series.</returns>
    public Task<RiskEquitySeries> EquityAsync(Guid accountId, DateTimeOffset start, DateTimeOffset? end = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskEquitySeries>($"/risk/accounts/{Id(accountId)}/equity", new Query().Add("from", start).Add("to", end), cancellationToken);

    /// <summary>What a close action would close now: the positions MFX Hub placed, or every one.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="scope">Which positions.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The positions it would close.</returns>
    public Task<RiskCloseScope> CloseScopeAsync(Guid accountId, RiskCloseScopeScope scope, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskCloseScope>($"/risk/accounts/{Id(accountId)}/close-scope", new Query().Add("scope", Wire.Word(scope)), cancellationToken);

    /// <summary>Takes the account out of Risk Management.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="reason">Why.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account.</returns>
    public Task<RiskAccount> DeactivateAsync(Guid accountId, string reason, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskAccount>($"/risk/accounts/{Id(accountId)}/deactivate", Body(("reason", reason)), idempotencyKey, cancellationToken);

    /// <summary>Clears a breach (<c>risk:override</c>).</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="rule">The rule to clear.</param>
    /// <param name="reason">Why.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account.</returns>
    public Task<RiskAccount> ClearAsync(Guid accountId, RiskClearRule rule, string reason, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskAccount>($"/risk/accounts/{Id(accountId)}/clear", Body(("rule", Wire.Word(rule)), ("reason", reason)), idempotencyKey, cancellationToken);

    /// <summary>The tenant's coverage, summed over its protected accounts.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The summary.</returns>
    public Task<RiskCoverageSummary> CoverageAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskCoverageSummary>("/risk/coverage", null, cancellationToken);

    // --- the planner ------------------------------------------------------------------------

    private async Task<T> AskAsync<T>(string path, JsonObject body, CancellationToken cancellationToken)
    {
        // A planner call changes nothing, so it is a read: tried again after a 429, no key.
        var answer = await _client.ReadAsync(new MfxRequest(HttpMethod.Post, path, null, Wire.Body(body)), cancellationToken).ConfigureAwait(false);
        return MfxClient.Body<T>(answer, "POST " + path);
    }

    /// <summary>Sizes a trade from a risk budget, and checks it against every rule; nothing is sent to the broker.</summary>
    /// <param name="request">The preview's request, in the contract's shape (<see cref="RiskPreviewRequest"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The preview.</returns>
    public Task<RiskPreview> PreviewAsync(object request, CancellationToken cancellationToken = default) =>
        AskAsync<RiskPreview>("/risk/planner/preview", Required(request, nameof(request)).AsObject(), cancellationToken);

    /// <summary>Checks an order already sized, as the pre-trade check would judge it; nothing is sent to the broker.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="order">The order, in the contract's shape (<see cref="RiskProposedOrder"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The verdict.</returns>
    public Task<RiskCheckResult> CheckAsync(Guid accountId, object order, CancellationToken cancellationToken = default) =>
        AskAsync<RiskCheckResult>("/risk/planner/check", Body(("account_id", Id(accountId)), ("order", Required(order, nameof(order)))), cancellationToken);

    // --- exit plans -------------------------------------------------------------------------

    /// <summary>
    /// Places an exit plan: steps on a position (move the stop, close part, trail), held by the broker or
    /// by MFX Hub, on a position already open or with the order that opens it.
    /// </summary>
    /// <param name="plan">The plan, in the contract's shape (<see cref="RiskPlanCreate"/>).</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="RiskRefusedException">The order with it breaks a rule in force.</exception>
    public Task<RiskPlan> CreatePlanAsync(object plan, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskPlan>("/risk/plans", Required(plan, nameof(plan)).AsObject(), idempotencyKey, cancellationToken);

    /// <summary>One plan, with <c>r_now</c>.</summary>
    /// <param name="planId">The plan.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan.</returns>
    public Task<RiskPlan> PlanAsync(Guid planId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskPlan>($"/risk/plans/{Id(planId)}", null, cancellationToken);

    private static Query PlansQuery(IEnumerable<Guid>? accountId, IEnumerable<RiskPlanState>? state, TimeSort sort) =>
        new Query().Repeated("account_id", accountId?.Select(Wire.Id)).Repeated("state", Wire.Words(state)).Add("sort", Wire.Word(sort));

    /// <summary>One page of plans and its <c>stats</c>, of several accounts and states, newest or oldest first.</summary>
    /// <param name="accountId">Only these accounts' plans.</param>
    /// <param name="state">Only plans in these states.</param>
    /// <param name="sort">Newest first by default.</param>
    /// <param name="cursor">The cursor of the page before.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<RiskPlanPage> PlansPageAsync(
        IEnumerable<Guid>? accountId = null, IEnumerable<RiskPlanState>? state = null, TimeSort sort = TimeSort.Newest,
        string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskPlanPage>("/risk/plans",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(PlansQuery(accountId, state, sort)), cancellationToken);

    /// <summary>Every plan, following the cursor, filtered as <see cref="PlansPageAsync"/>.</summary>
    /// <param name="accountId">As <see cref="PlansPageAsync"/>.</param>
    /// <param name="state">As <see cref="PlansPageAsync"/>.</param>
    /// <param name="sort">As <see cref="PlansPageAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every plan.</returns>
    public IAsyncEnumerable<RiskPlan> PlansAsync(
        IEnumerable<Guid>? accountId = null, IEnumerable<RiskPlanState>? state = null, TimeSort sort = TimeSort.Newest,
        CancellationToken cancellationToken = default)
    {
        var filters = PlansQuery(accountId, state, sort);
        return Pages.All((cursor, token) => _client.PageAsync<RiskPlan>("/risk/plans", new Query().Add("cursor", cursor).AddRange(filters), token),
            cancellationToken);
    }

    /// <summary>Replaces a plan's steps.</summary>
    /// <param name="planId">The plan.</param>
    /// <param name="steps">The steps, each in the contract's shape (<see cref="RiskPlanStepSpec"/>).</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan.</returns>
    public Task<RiskPlan> AmendPlanAsync(Guid planId, IEnumerable<object> steps, string idempotencyKey, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var list = new JsonArray([.. steps.Select(s => Required(s, nameof(steps)))]);
        return Post<RiskPlan>($"/risk/plans/{Id(planId)}/amend", Body(("steps", list), ("reason", reason)), idempotencyKey, cancellationToken);
    }

    /// <summary>Pauses a plan.</summary>
    /// <param name="planId">The plan.</param>
    /// <param name="reason">Why.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan.</returns>
    public Task<RiskPlan> PausePlanAsync(Guid planId, string reason, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskPlan>($"/risk/plans/{Id(planId)}/pause", Body(("reason", reason)), idempotencyKey, cancellationToken);

    /// <summary>Resumes a paused plan.</summary>
    /// <param name="planId">The plan.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan.</returns>
    public Task<RiskPlan> ResumePlanAsync(Guid planId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskPlan>($"/risk/plans/{Id(planId)}/resume", null, idempotencyKey, cancellationToken);

    /// <summary>Ends a plan: its steps stop; the position is left as it is.</summary>
    /// <param name="planId">The plan.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan.</returns>
    public Task<RiskPlan> EndPlanAsync(Guid planId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _client.WriteAsync<RiskPlan>(HttpMethod.Delete, $"/risk/plans/{Id(planId)}", null, Keys.Required(idempotencyKey), cancellationToken);

    // --- challenges -------------------------------------------------------------------------

    /// <summary>Creates a challenge: one versioned rule set over many accounts, each passing or failing on its own.</summary>
    /// <param name="name">The challenge's name.</param>
    /// <param name="rules">The rules (<see cref="RiskChallengeRules"/>, or its shape).</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The challenge.</returns>
    public Task<RiskChallenge> CreateChallengeAsync(string name, object rules, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Post<RiskChallenge>("/risk/challenges", Body(("name", name), ("rules", Required(rules, nameof(rules)))), idempotencyKey, cancellationToken);
    }

    /// <summary>Adds a version to a challenge's rules.</summary>
    /// <param name="challengeId">The challenge.</param>
    /// <param name="rules">The new rules.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The challenge.</returns>
    public Task<RiskChallenge> AddChallengeVersionAsync(Guid challengeId, object rules, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskChallenge>($"/risk/challenges/{Id(challengeId)}/versions", Required(rules, nameof(rules)).AsObject(), idempotencyKey, cancellationToken);

    /// <summary>
    /// Archives a challenge. Refused with <see cref="StateException"/> while any account is in progress
    /// (<c>details.accounts</c> names them); archiving it again answers it unchanged.
    /// </summary>
    /// <param name="challengeId">The challenge.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The challenge, with its <c>archived_at</c>.</returns>
    public Task<RiskChallenge> ArchiveChallengeAsync(Guid challengeId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskChallenge>($"/risk/challenges/{Id(challengeId)}/archive", null, idempotencyKey, cancellationToken);

    /// <summary>One challenge.</summary>
    /// <param name="challengeId">The challenge.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The challenge.</returns>
    public Task<RiskChallenge> ChallengeAsync(Guid challengeId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskChallenge>($"/risk/challenges/{Id(challengeId)}", null, cancellationToken);

    /// <summary>Every challenge, following the cursor.</summary>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every challenge.</returns>
    public IAsyncEnumerable<RiskChallenge> ChallengesAsync(int? limit = null, CancellationToken cancellationToken = default) =>
        Pages.All((cursor, token) => _client.PageAsync<RiskChallenge>("/risk/challenges", new Query().Add("cursor", cursor).Add("limit", limit), token),
            cancellationToken);

    /// <summary>Enrols accounts in a challenge; each is judged separately (one with a stale read is refused <c>risk_inputs_stale</c>).</summary>
    /// <param name="challengeId">The challenge.</param>
    /// <param name="accountIds">The accounts.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One outcome per account.</returns>
    public Task<RiskActivationResult> EnrolAsync(Guid challengeId, IEnumerable<Guid> accountIds, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Post<RiskActivationResult>($"/risk/challenges/{Id(challengeId)}/accounts", Body(("account_ids", Ids(accountIds, nameof(accountIds)))),
            idempotencyKey, cancellationToken);

    private static Query ChallengeAccountsQuery(
        IEnumerable<RiskChallengeAccountState>? state, IEnumerable<RiskChallengeRule>? rule, bool? belowMinDays, RiskChallengeSort sort) =>
        new Query().Repeated("state", Wire.Words(state)).Repeated("rule", Wire.Words(rule)).Add("below_min_days", belowMinDays).Add("sort", Wire.Word(sort));

    /// <summary>One page of the challenge's accounts, with <c>stats</c> over the filtered set. This list, paged, is the export.</summary>
    /// <param name="challengeId">The challenge.</param>
    /// <param name="state">Only accounts in these states.</param>
    /// <param name="rule">Only accounts failing on these rules.</param>
    /// <param name="belowMinDays">Only accounts below the minimum trading days, or only those not.</param>
    /// <param name="sort">Daily loss used by default.</param>
    /// <param name="cursor">The cursor of the page before.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<RiskChallengeAccountPage> ChallengeAccountsPageAsync(
        Guid challengeId, IEnumerable<RiskChallengeAccountState>? state = null, IEnumerable<RiskChallengeRule>? rule = null,
        bool? belowMinDays = null, RiskChallengeSort sort = RiskChallengeSort.DailyLossUsed, string? cursor = null, int? limit = null,
        CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskChallengeAccountPage>($"/risk/challenges/{Id(challengeId)}/accounts",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(ChallengeAccountsQuery(state, rule, belowMinDays, sort)), cancellationToken);

    /// <summary>Every account of the challenge, in the order <paramref name="sort"/> names.</summary>
    /// <param name="challengeId">The challenge.</param>
    /// <param name="state">As <see cref="ChallengeAccountsPageAsync"/>.</param>
    /// <param name="rule">As <see cref="ChallengeAccountsPageAsync"/>.</param>
    /// <param name="belowMinDays">As <see cref="ChallengeAccountsPageAsync"/>.</param>
    /// <param name="sort">As <see cref="ChallengeAccountsPageAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every account.</returns>
    public IAsyncEnumerable<RiskChallengeAccount> ChallengeAccountsAsync(
        Guid challengeId, IEnumerable<RiskChallengeAccountState>? state = null, IEnumerable<RiskChallengeRule>? rule = null,
        bool? belowMinDays = null, RiskChallengeSort sort = RiskChallengeSort.DailyLossUsed, CancellationToken cancellationToken = default)
    {
        var filters = ChallengeAccountsQuery(state, rule, belowMinDays, sort);
        return Pages.All((cursor, token) => _client.PageAsync<RiskChallengeAccount>($"/risk/challenges/{Id(challengeId)}/accounts",
            new Query().Add("cursor", cursor).AddRange(filters), token), cancellationToken);
    }

    /// <summary>One enrolled account, its result and its periods with their statistics.</summary>
    /// <param name="challengeId">The challenge.</param>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account's detail.</returns>
    public Task<RiskChallengeAccountDetail> ChallengeAccountAsync(Guid challengeId, Guid accountId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskChallengeAccountDetail>($"/risk/challenges/{Id(challengeId)}/accounts/{Id(accountId)}", null, cancellationToken);

    // --- activity ---------------------------------------------------------------------------

    private static Query ActivityQuery(
        IEnumerable<RiskActivityKind>? kind, IEnumerable<Guid>? accountId, IEnumerable<RiskOutcome>? outcome,
        DateTimeOffset? start, DateTimeOffset? end, RiskActivitySort sort) =>
        new Query().Repeated("kind", Wire.Words(kind)).Repeated("account_id", accountId?.Select(Wire.Id)).Repeated("outcome", Wire.Words(outcome))
            .Add("from", start).Add("to", end).Add("sort", Wire.Word(sort));

    /// <summary>One page of activity (every warning, breach, action and change), unresolved first by default, with <c>stats</c>.</summary>
    /// <param name="kind">Only these kinds.</param>
    /// <param name="accountId">Only these accounts.</param>
    /// <param name="outcome">Only these outcomes.</param>
    /// <param name="start">From this instant.</param>
    /// <param name="end">To this instant.</param>
    /// <param name="sort">Unresolved first by default.</param>
    /// <param name="cursor">The cursor of the page before.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<RiskActivityPage> ActivityPageAsync(
        IEnumerable<RiskActivityKind>? kind = null, IEnumerable<Guid>? accountId = null, IEnumerable<RiskOutcome>? outcome = null,
        DateTimeOffset? start = null, DateTimeOffset? end = null, RiskActivitySort sort = RiskActivitySort.UnresolvedFirst,
        string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<RiskActivityPage>("/risk/activity",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(ActivityQuery(kind, accountId, outcome, start, end, sort)), cancellationToken);

    /// <summary>Every warning, breach, action and change, following the cursor to the end.</summary>
    /// <param name="kind">As <see cref="ActivityPageAsync"/>.</param>
    /// <param name="accountId">As <see cref="ActivityPageAsync"/>.</param>
    /// <param name="outcome">As <see cref="ActivityPageAsync"/>.</param>
    /// <param name="start">As <see cref="ActivityPageAsync"/>.</param>
    /// <param name="end">As <see cref="ActivityPageAsync"/>.</param>
    /// <param name="sort">As <see cref="ActivityPageAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every row of activity.</returns>
    public IAsyncEnumerable<RiskActivity> ActivityAsync(
        IEnumerable<RiskActivityKind>? kind = null, IEnumerable<Guid>? accountId = null, IEnumerable<RiskOutcome>? outcome = null,
        DateTimeOffset? start = null, DateTimeOffset? end = null, RiskActivitySort sort = RiskActivitySort.UnresolvedFirst,
        CancellationToken cancellationToken = default)
    {
        var filters = ActivityQuery(kind, accountId, outcome, start, end, sort);
        return Pages.All((cursor, token) => _client.PageAsync<RiskActivity>("/risk/activity", new Query().Add("cursor", cursor).AddRange(filters), token),
            cancellationToken);
    }
}
