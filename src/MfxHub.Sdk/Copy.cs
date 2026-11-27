using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MfxHub.Models;

namespace MfxHub;

/// <summary>The copy log's order: newest first, or the slowest of MFX Hub's part first.</summary>
public enum CopyLogSort
{
    /// <summary>Newest first.</summary>
    [JsonStringEnumMemberName("created_at")] CreatedAt,

    /// <summary>The slowest of MFX Hub's part first.</summary>
    [JsonStringEnumMemberName("mfx_ms")] MfxMs,
}

/// <summary>A template field a group's rollover can set on its members.</summary>
public enum CopyRolloverField
{
    /// <summary>The scaling.</summary>
    [JsonStringEnumMemberName("scaling")] Scaling,

    /// <summary>The bounds.</summary>
    [JsonStringEnumMemberName("bounds")] Bounds,

    /// <summary>The options.</summary>
    [JsonStringEnumMemberName("options")] Options,

    /// <summary>The risk limits.</summary>
    [JsonStringEnumMemberName("risk_limits")] RiskLimits,

    /// <summary>Whether to close on a stop-out.</summary>
    [JsonStringEnumMemberName("close_on_stopout")] CloseOnStopout,
}

/// <summary>
/// A change to a field an update may also clear: <see cref="Clear"/> sends null, which clears it;
/// a value (converted implicitly) sets it; leaving the argument null leaves the field as it is.
/// </summary>
/// <typeparam name="T">The field's type.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
    Justification = "Change<decimal>.Clear reads as the call it is; the three states are left as it is, cleared and a value")]
public sealed record Change<T>
{
    private Change(T? value, bool clear) => (Value, IsClear) = (value, clear);

    /// <summary>Clears the field.</summary>
    public static Change<T> Clear { get; } = new(default, clear: true);

    /// <summary>Sets the field to <paramref name="value"/>.</summary>
    /// <param name="value">The new value.</param>
    /// <returns>The change.</returns>
    public static Change<T> To(T value) => new(value, clear: false);

    /// <summary>A value converts to a change that sets it.</summary>
    /// <param name="value">The new value.</param>
    public static implicit operator Change<T>(T value) => To(value);

    /// <summary>The new value; default when the field is cleared.</summary>
    public T? Value { get; }

    /// <summary>True when the field is cleared.</summary>
    public bool IsClear { get; }
}

/// <summary>
/// Copy Trading for this key's tenant (<c>client.Copy</c>): leaders, followers, groups, stop-outs,
/// requested closes, signals and the copy log.
/// </summary>
/// <remarks>
/// One of the tenant's accounts, or an external signal source, leads; other accounts follow it, each
/// with its own scaling, bounds, symbol map, options and limits. The tenant chooses them; MFX Hub
/// executes and suggests none. Reads need <c>copy:read</c> and writes <c>copy:write</c>; every write
/// takes the caller's idempotency key and is sent once. A nested setting (<c>scaling</c>, <c>bounds</c>,
/// <c>options</c>, <c>risk_limits</c>, <c>filters</c>, <c>copies</c>, <c>limits</c>, <c>who_may_join</c>,
/// <c>template</c>, a preview's rows) is the generated model or anything in the contract's own names
/// (<c>new { mode = "fixed_multiplier", multiplier = 0.5m }</c>): a decimal goes as text, a
/// <see cref="double"/> anywhere is refused, and a name the contract does not have or a word outside its
/// list is refused, all before anything is sent. A list page is the contract's page model with its
/// <c>stats</c> row, counted over everything the filters match; an <c>All...</c> method follows the cursor.
/// </remarks>
public sealed class Copy
{
    /// <summary>Why a copy did not fill: the contract's closed list, in its order. <c>broker</c> comes with the venue's code beside it.</summary>
    public static readonly IReadOnlyList<string> RefusalReasons = [.. Enum.GetValues<CopyReason>().Where(r => !UnknownEnumValue.Is(r)).Select(r => Wire.Word(r))];

    private const int MembersMax = 500;

    private readonly MfxClient _client;

    internal Copy(MfxClient client) => _client = client;

    private static string Id(Guid id) => Wire.Id(id);

    /// <summary>A body of the fields given, checked against <typeparamref name="TModel"/>; a field left null is not sent.</summary>
    private static JsonObject Body<TModel>(string name, params (string Field, object? Value)[] fields)
    {
        var body = new JsonObject();
        foreach (var (field, value) in fields)
        {
            switch (value)
            {
                case null:
                    break;
                case Cleared:
                    body[field] = null;
                    break;
                default:
                    body[field] = Wire.Setting(value, $"{name}.{field}");
                    break;
            }
        }
        return Shape.Of<TModel>(body, name);
    }

    /// <summary>A field sent as null, which clears it.</summary>
    private sealed class Cleared
    {
        public static readonly Cleared Value = new();
    }

    private Task<T> Write<T>(HttpMethod method, string path, JsonObject? body, string idempotencyKey, CancellationToken cancellationToken) =>
        _client.WriteAsync<T>(method, path, body, Keys.Required(idempotencyKey), cancellationToken);

    private static void Window(string what, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is { } start && to is { } end && end < start)
        {
            throw new ArgumentException($"{what}'s window ends before it starts", nameof(to));
        }
    }

    private static string? Joined<TEnum>(IEnumerable<TEnum>? values, string parameter)
        where TEnum : struct, Enum => WireNames.Joined(values, parameter);

    /// <summary>A decimal that may be negative, as the contract's <c>CopyDecimal</c> allows: a stop loss or take profit is a price.</summary>
    private static string? Signed(decimal? value) => value is { } exact ? Wire.Decimal(exact) : null;

    // --- leaders ----------------------------------------------------------------------------

    /// <summary>
    /// Makes one of the tenant's accounts (source <c>account</c>, with <paramref name="accountId"/>) or an
    /// external signal source (source <c>external</c>) a leader. <paramref name="signalLifetimeS"/> (1 to
    /// 3600) is how long a signal may still be copied after MFX Hub makes it. An account leads once.
    /// </summary>
    /// <returns>The leader.</returns>
    public Task<CopyLeader> CreateLeaderAsync(
        CopyLeaderSource source, int signalLifetimeS, string idempotencyKey, Guid? accountId = null, object? filters = null,
        object? copies = null, object? limits = null, object? whoMayJoin = null, CancellationToken cancellationToken = default)
    {
        var body = Body<CopyLeaderCreate>("create_leader", ("source", Wire.Word(source)), ("signal_lifetime_s", signalLifetimeS),
            ("account_id", accountId), ("filters", filters), ("copies", copies), ("limits", limits), ("who_may_join", whoMayJoin));
        return Write<CopyLeader>(HttpMethod.Post, "/copy/leaders", body, idempotencyKey, cancellationToken);
    }

    /// <summary>One leader; another tenant's id is a <see cref="NotFoundException"/>.</summary>
    /// <returns>The leader.</returns>
    public Task<CopyLeader> GetLeaderAsync(Guid leaderId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyLeader>($"/copy/leaders/{Id(leaderId)}", null, cancellationToken);

    private static Query LeadersQuery(IEnumerable<CopyLeaderState>? state, CopyLeaderSource? source, Guid? accountId) =>
        new Query().Add("state", Joined(state, nameof(state))).Add("source", source is { } s ? Wire.Word(s) : null).Add("account_id", accountId);

    /// <summary>One page of the tenant's leaders, newest first, with the count of each state in <c>stats</c>; several states go comma-separated.</summary>
    /// <returns>The page.</returns>
    public Task<CopyLeaderPage> LeadersAsync(
        string? cursor = null, int? limit = null, IEnumerable<CopyLeaderState>? state = null, CopyLeaderSource? source = null,
        Guid? accountId = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyLeaderPage>("/copy/leaders",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(LeadersQuery(state, source, accountId)), cancellationToken);

    /// <summary>Every leader, following the cursor, narrowed as <see cref="LeadersAsync"/> is.</summary>
    /// <returns>Every leader.</returns>
    public IAsyncEnumerable<CopyLeader> AllLeadersAsync(
        int? limit = null, IEnumerable<CopyLeaderState>? state = null, CopyLeaderSource? source = null, Guid? accountId = null,
        CancellationToken cancellationToken = default)
    {
        var filters = LeadersQuery(state, source, accountId);
        return Pages.All((cursor, token) => _client.PageAsync<CopyLeader>("/copy/leaders",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(filters), token), cancellationToken);
    }

    /// <summary>Changes what a leader's followers receive; only the fields given are sent and change. Applies to the leader's next deal.</summary>
    /// <returns>The leader.</returns>
    public Task<CopyLeader> UpdateLeaderAsync(
        Guid leaderId, string idempotencyKey, object? filters = null, object? copies = null, int? signalLifetimeS = null,
        object? limits = null, object? whoMayJoin = null, CancellationToken cancellationToken = default)
    {
        var body = Body<CopyLeaderUpdate>("update_leader", ("filters", filters), ("copies", copies), ("signal_lifetime_s", signalLifetimeS),
            ("limits", limits), ("who_may_join", whoMayJoin));
        return Write<CopyLeader>(HttpMethod.Patch, $"/copy/leaders/{Id(leaderId)}", body, idempotencyKey, cancellationToken);
    }

    /// <summary>Stops copying the leader's new entries; its closes and stop changes are still copied.</summary>
    /// <returns>The leader.</returns>
    public Task<CopyLeader> PauseLeaderAsync(Guid leaderId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyLeader>(HttpMethod.Post, $"/copy/leaders/{Id(leaderId)}/pause", null, idempotencyKey, cancellationToken);

    /// <summary>Copies the leader's entries again, from its next deal.</summary>
    /// <returns>The leader.</returns>
    public Task<CopyLeader> ResumeLeaderAsync(Guid leaderId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyLeader>(HttpMethod.Post, $"/copy/leaders/{Id(leaderId)}/resume", null, idempotencyKey, cancellationToken);

    /// <summary>
    /// Retires a leader: every follow of it ends. With <paramref name="closeCopied"/> each follower's
    /// volume copied from this leader is closed, never the account's own positions; it has no default,
    /// because closing positions is a choice the caller makes. Retired is final.
    /// </summary>
    /// <returns>The leader.</returns>
    public Task<CopyLeader> RetireLeaderAsync(Guid leaderId, bool closeCopied, string reason, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyLeader>(HttpMethod.Post, $"/copy/leaders/{Id(leaderId)}/retire",
            Body<CopyLeaderRetire>("retire_leader", ("close_copied", closeCopied), ("reason", reason)), idempotencyKey, cancellationToken);

    /// <summary>
    /// An external leader opens a position (a market entry), copied to its followers. <paramref name="id"/>
    /// is the caller's id for the signal, unique for the leader; a close names it. Exactly one of
    /// <paramref name="volume"/> (lots) and <paramref name="units"/> (base units) is given, or it is refused
    /// here. <paramref name="sl"/> and <paramref name="tp"/> may be below zero.
    /// </summary>
    /// <returns>The accepted signal.</returns>
    public Task<CopySignalAccepted> SendOpenSignalAsync(
        Guid leaderId, string id, string symbol, Side side, string idempotencyKey, decimal? volume = null, decimal? units = null,
        string? instrumentKey = null, decimal? sl = null, decimal? tp = null, DateTimeOffset? time = null, object? account = null,
        CancellationToken cancellationToken = default)
    {
        // DECISION: both or neither of volume and units is refused here, before the wire.
        if ((volume is null) == (units is null))
        {
            throw new ArgumentException("an open signal takes exactly one of volume and units");
        }
        var body = Body<CopyExternalOpen>("send_open_signal", ("id", id), ("symbol", symbol), ("side", Wire.Word(side)),
            ("volume", volume is { } v ? Commands.Amount(v, nameof(volume)) : null), ("units", units is { } u ? Commands.Amount(u, nameof(units)) : null),
            ("instrument_key", instrumentKey), ("sl", Signed(sl)), ("tp", Signed(tp)), ("time", time is { } t ? Wire.Instant(t) : null), ("account", account));
        return Write<CopySignalAccepted>(HttpMethod.Post, $"/copy/leaders/{Id(leaderId)}/signals", body, idempotencyKey, cancellationToken);
    }

    /// <summary>An external leader closes the position an earlier open made: <paramref name="close"/> is that open's id.</summary>
    /// <returns>The accepted signal.</returns>
    public Task<CopySignalAccepted> SendCloseSignalAsync(
        Guid leaderId, string id, string close, string idempotencyKey, DateTimeOffset? time = null, CancellationToken cancellationToken = default) =>
        Write<CopySignalAccepted>(HttpMethod.Post, $"/copy/leaders/{Id(leaderId)}/signals",
            Body<CopyExternalClose>("send_close_signal", ("id", id), ("close", close), ("time", time is { } t ? Wire.Instant(t) : null)),
            idempotencyKey, cancellationToken);

    // --- followers --------------------------------------------------------------------------

    /// <summary>
    /// Makes one of the tenant's accounts follow a leader, with its own scaling and limits. The follow is
    /// checked once, here, against the account's venue; preview it first with <see cref="PreviewFollowerAsync"/>.
    /// </summary>
    /// <returns>The follower.</returns>
    public Task<CopyFollower> CreateFollowerAsync(
        Guid accountId, Guid leaderId, string idempotencyKey, object? scaling = null, object? bounds = null,
        IReadOnlyDictionary<string, string>? symbolMap = null, object? options = null, object? riskLimits = null,
        bool? closeOnStopout = null, Guid? groupId = null, CancellationToken cancellationToken = default)
    {
        var body = Body<CopyFollowerCreate>("create_follower", ("account_id", accountId), ("leader_id", leaderId), ("scaling", scaling),
            ("bounds", bounds), ("symbol_map", symbolMap), ("options", options), ("risk_limits", riskLimits),
            ("close_on_stopout", closeOnStopout), ("group_id", groupId));
        return Write<CopyFollower>(HttpMethod.Post, "/copy/followers", body, idempotencyKey, cancellationToken);
    }

    /// <summary>
    /// What a follow would do before it is made: for each row (a leader symbol and volume), the follower
    /// symbol, how it was matched and the follower's volume, or why it would be refused. A read: nothing is
    /// created and no idempotency key is taken. 1 to 200 rows.
    /// </summary>
    /// <returns>The preview.</returns>
    public async Task<CopyPreview> PreviewFollowerAsync(
        Guid accountId, Guid leaderId, object scaling, IEnumerable<object> rows, object? bounds = null,
        IReadOnlyDictionary<string, string>? symbolMap = null, object? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scaling);
        var list = rows?.ToList() ?? throw new ArgumentNullException(nameof(rows));
        if (list.Count == 0)
        {
            throw new ArgumentException("a preview needs at least one row", nameof(rows));
        }
        var body = Body<CopyPreviewRequest>("preview_follower", ("account_id", accountId), ("leader_id", leaderId), ("scaling", scaling),
            ("rows", list), ("bounds", bounds), ("symbol_map", symbolMap), ("options", options));
        // DECISION: a preview creates nothing, so it is sent as a read: tried again after a 429.
        var answer = await _client.ReadAsync(new MfxRequest(HttpMethod.Post, "/copy/followers/preview", null, Wire.Body(body)), cancellationToken)
            .ConfigureAwait(false);
        return MfxClient.Body<CopyPreview>(answer, "POST /copy/followers/preview");
    }

    /// <summary>One follower: its mandate and version, its state and the stop-outs in force.</summary>
    /// <returns>The follower.</returns>
    public Task<CopyFollower> GetFollowerAsync(Guid followerId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyFollower>($"/copy/followers/{Id(followerId)}", null, cancellationToken);

    private static Query FollowersQuery(
        IEnumerable<CopyFollowerState>? state, Guid? leaderId, Guid? accountId, Guid? groupId, bool? outOfStep, TimeSort sort) =>
        new Query().Add("state", Joined(state, nameof(state))).Add("leader_id", leaderId).Add("account_id", accountId).Add("group_id", groupId)
            .Add("sort", Wire.Word(sort)).Add("out_of_step", outOfStep);

    /// <summary>
    /// One page of the tenant's followers, newest first (or oldest), with the count of each state and of
    /// those out of step in <c>stats</c>. <paramref name="outOfStep"/> true is the followers whose copy host
    /// does not hold their latest mandate.
    /// </summary>
    /// <returns>The page.</returns>
    public Task<CopyFollowerPage> FollowersAsync(
        string? cursor = null, int? limit = null, IEnumerable<CopyFollowerState>? state = null, Guid? leaderId = null, Guid? accountId = null,
        Guid? groupId = null, bool? outOfStep = null, TimeSort sort = TimeSort.Newest, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyFollowerPage>("/copy/followers",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(FollowersQuery(state, leaderId, accountId, groupId, outOfStep, sort)),
            cancellationToken);

    /// <summary>Every follower, following the cursor, narrowed as <see cref="FollowersAsync"/> is.</summary>
    /// <returns>Every follower.</returns>
    public IAsyncEnumerable<CopyFollower> AllFollowersAsync(
        int? limit = null, IEnumerable<CopyFollowerState>? state = null, Guid? leaderId = null, Guid? accountId = null, Guid? groupId = null,
        bool? outOfStep = null, TimeSort sort = TimeSort.Newest, CancellationToken cancellationToken = default)
    {
        var filters = FollowersQuery(state, leaderId, accountId, groupId, outOfStep, sort);
        return Pages.All((cursor, token) => _client.PageAsync<CopyFollower>("/copy/followers",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(filters), token), cancellationToken);
    }

    /// <summary>Changes a follower's mandate; only the fields given are sent, and each change is a new mandate version.</summary>
    /// <returns>The follower.</returns>
    public Task<CopyFollower> UpdateFollowerAsync(
        Guid followerId, string idempotencyKey, object? scaling = null, object? bounds = null, IReadOnlyDictionary<string, string>? symbolMap = null,
        object? options = null, object? riskLimits = null, bool? closeOnStopout = null, CancellationToken cancellationToken = default)
    {
        var body = Body<CopyFollowerUpdate>("update_follower", ("scaling", scaling), ("bounds", bounds), ("symbol_map", symbolMap),
            ("options", options), ("risk_limits", riskLimits), ("close_on_stopout", closeOnStopout));
        return Write<CopyFollower>(HttpMethod.Patch, $"/copy/followers/{Id(followerId)}", body, idempotencyKey, cancellationToken);
    }

    /// <summary>Stops copying new entries to this follower; closes of what it copied still are.</summary>
    /// <returns>The follower.</returns>
    public Task<CopyFollower> PauseFollowerAsync(Guid followerId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyFollower>(HttpMethod.Post, $"/copy/followers/{Id(followerId)}/pause", null, idempotencyKey, cancellationToken);

    /// <summary>Copies to this follower again from the leader's next deal; stopped out while a stop-out is still active.</summary>
    /// <returns>The follower.</returns>
    public Task<CopyFollower> ResumeFollowerAsync(Guid followerId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyFollower>(HttpMethod.Post, $"/copy/followers/{Id(followerId)}/resume", null, idempotencyKey, cancellationToken);

    /// <summary>Sends the follower's mandate to its copy host again and heals its copied positions from the account; nothing is traded.</summary>
    /// <returns>The follower.</returns>
    public Task<CopyFollower> ResyncFollowerAsync(Guid followerId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyFollower>(HttpMethod.Post, $"/copy/followers/{Id(followerId)}/resync", null, idempotencyKey, cancellationToken);

    /// <summary>
    /// Ends the follow (final). With <paramref name="closeCopied"/> the volume copied from this leader is
    /// closed, never the account's own; it has no default. The close is a requested close, on the answer.
    /// </summary>
    /// <returns>The follower.</returns>
    public Task<CopyFollower> UnfollowAsync(Guid followerId, bool closeCopied, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyFollower>(HttpMethod.Post, $"/copy/followers/{Id(followerId)}/unfollow",
            Body<CopyUnfollow>("unfollow", ("close_copied", closeCopied)), idempotencyKey, cancellationToken);

    /// <summary>One page of a follower's stop-outs, newest first.</summary>
    /// <returns>The page.</returns>
    public Task<Page<CopyStopout>> StopoutsAsync(
        Guid followerId, string? cursor = null, int? limit = null, IEnumerable<CopyStopoutState>? state = null, CancellationToken cancellationToken = default) =>
        _client.PageAsync<CopyStopout>($"/copy/followers/{Id(followerId)}/stopouts",
            new Query().Add("cursor", cursor).Add("limit", limit).Add("state", Joined(state, nameof(state))), cancellationToken);

    /// <summary>Every stop-out of a follower, following the cursor.</summary>
    /// <returns>Every stop-out.</returns>
    public IAsyncEnumerable<CopyStopout> AllStopoutsAsync(
        Guid followerId, int? limit = null, IEnumerable<CopyStopoutState>? state = null, CancellationToken cancellationToken = default)
    {
        var states = state?.ToList();
        return Pages.All((cursor, token) => StopoutsAsync(followerId, cursor, limit, states, token), cancellationToken);
    }

    /// <summary>Resets a follower's active stop-outs before their period ends, or only the one for <paramref name="limit"/>.</summary>
    /// <returns>The follower.</returns>
    public Task<CopyFollower> ResetStopoutsAsync(
        Guid followerId, string reason, string idempotencyKey, CopyLimitKind? limit = null, CancellationToken cancellationToken = default) =>
        Write<CopyFollower>(HttpMethod.Post, $"/copy/followers/{Id(followerId)}/stopouts/reset",
            Body<CopyStopoutReset>("reset_stopouts", ("reason", reason), ("limit", limit is { } l ? Wire.Word(l) : null)), idempotencyKey, cancellationToken);

    // --- groups -----------------------------------------------------------------------------

    /// <summary>
    /// A group of followers managed together under one template and one exposure cap (Enterprise).
    /// <paramref name="grossExposureCap"/> is in US dollars; <paramref name="placementPolicy"/> is deprecated:
    /// stored and returned, never read by placement.
    /// </summary>
    /// <returns>The group.</returns>
    public Task<CopyGroup> CreateGroupAsync(
        string name, string idempotencyKey, object? template = null, decimal? grossExposureCap = null, string? placementPolicy = null,
        CancellationToken cancellationToken = default)
    {
        var body = Body<CopyGroupCreate>("create_group", ("name", name), ("template", template),
            ("gross_exposure_cap", grossExposureCap is { } cap ? Commands.Amount(cap, nameof(grossExposureCap)) : null), ("placement_policy", placementPolicy));
        return Write<CopyGroup>(HttpMethod.Post, "/copy/groups", body, idempotencyKey, cancellationToken);
    }

    /// <summary>One group, with its template, exposure cap and how many followers it has.</summary>
    /// <returns>The group.</returns>
    public Task<CopyGroup> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyGroup>($"/copy/groups/{Id(groupId)}", null, cancellationToken);

    /// <summary>One page of the tenant's groups, newest first, with the count of each state in <c>stats</c>.</summary>
    /// <returns>The page.</returns>
    public Task<CopyGroupPage> GroupsAsync(string? cursor = null, int? limit = null, CopyGroupState? state = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyGroupPage>("/copy/groups",
            new Query().Add("cursor", cursor).Add("limit", limit).Add("state", state is { } s ? Wire.Word(s) : null), cancellationToken);

    /// <summary>Every group, following the cursor.</summary>
    /// <returns>Every group.</returns>
    public IAsyncEnumerable<CopyGroup> AllGroupsAsync(int? limit = null, CopyGroupState? state = null, CancellationToken cancellationToken = default)
    {
        var kept = state is { } s ? Wire.Word(s) : null;
        return Pages.All((cursor, token) => _client.PageAsync<CopyGroup>("/copy/groups",
            new Query().Add("cursor", cursor).Add("limit", limit).Add("state", kept), token), cancellationToken);
    }

    /// <summary>
    /// Changes a group's name, template, exposure cap or placement policy; only what is given is sent.
    /// <see cref="Change{T}.Clear"/> clears the cap or the policy. A new template applies to followers
    /// created into the group afterwards; <see cref="RolloverGroupAsync"/> sets it on its members.
    /// </summary>
    /// <returns>The group.</returns>
    public Task<CopyGroup> UpdateGroupAsync(
        Guid groupId, string idempotencyKey, string? name = null, object? template = null, Change<decimal>? grossExposureCap = null,
        Change<string>? placementPolicy = null, CancellationToken cancellationToken = default)
    {
        object? cap = grossExposureCap switch
        {
            null => null,
            { IsClear: true } => Cleared.Value,
            { Value: var v } => Commands.Amount(v, nameof(grossExposureCap)),
        };
        object? policy = placementPolicy switch
        {
            null => null,
            { IsClear: true } => Cleared.Value,
            { Value: var v } => v,
        };
        var body = Body<CopyGroupUpdate>("update_group", ("name", name), ("template", template), ("gross_exposure_cap", cap), ("placement_policy", policy));
        return Write<CopyGroup>(HttpMethod.Patch, $"/copy/groups/{Id(groupId)}", body, idempotencyKey, cancellationToken);
    }

    private static JsonObject Members(string name, IEnumerable<Guid> followerIds)
    {
        ArgumentNullException.ThrowIfNull(followerIds);
        var ids = followerIds.Select(Wire.Id).ToList();
        if (ids.Count is < 1 or > MembersMax)
        {
            throw new ArgumentException($"{name} takes 1 to {MembersMax} follower ids, not {ids.Count}", nameof(followerIds));
        }
        return Body<CopyGroupMembers>(name, ("follower_ids", ids));
    }

    /// <summary>Adds followers to a group, 1 to 500 at once; each follower's outcome is answered apart. Mandates are not changed.</summary>
    /// <returns>Each follower's outcome.</returns>
    public Task<CopyGroupMembersResult> AddGroupFollowersAsync(
        Guid groupId, IEnumerable<Guid> followerIds, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyGroupMembersResult>(HttpMethod.Post, $"/copy/groups/{Id(groupId)}/followers", Members("add_group_followers", followerIds),
            idempotencyKey, cancellationToken);

    /// <summary>Takes followers out of a group; they keep following with their own mandates.</summary>
    /// <returns>Each follower's outcome.</returns>
    public Task<CopyGroupMembersResult> RemoveGroupFollowersAsync(
        Guid groupId, IEnumerable<Guid> followerIds, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyGroupMembersResult>(HttpMethod.Post, $"/copy/groups/{Id(groupId)}/followers/remove", Members("remove_group_followers", followerIds),
            idempotencyKey, cancellationToken);

    /// <summary>Stops copying new entries to every follower of the group; closes still are.</summary>
    /// <returns>The group.</returns>
    public Task<CopyGroup> PauseGroupAsync(Guid groupId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyGroup>(HttpMethod.Post, $"/copy/groups/{Id(groupId)}/pause", null, idempotencyKey, cancellationToken);

    /// <summary>Copies to the group's followers again, from the leader's next deal.</summary>
    /// <returns>The group.</returns>
    public Task<CopyGroup> ResumeGroupAsync(Guid groupId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyGroup>(HttpMethod.Post, $"/copy/groups/{Id(groupId)}/resume", null, idempotencyKey, cancellationToken);

    /// <summary>
    /// Closes the copied positions of every live member of the group (Enterprise); the follows are kept.
    /// Each member gets one requested close, all under one <c>batch_id</c>. <paramref name="pause"/> has no
    /// default: true pauses the group in the same act.
    /// </summary>
    /// <returns>The batch and its closes.</returns>
    public Task<CopyGroupCloseResult> CloseGroupAsync(Guid groupId, string reason, bool pause, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Write<CopyGroupCloseResult>(HttpMethod.Post, $"/copy/groups/{Id(groupId)}/close",
            Body<CopyGroupClose>("close_group", ("reason", reason), ("pause", pause)), idempotencyKey, cancellationToken);

    /// <summary>
    /// Sets the group's template on its live members, or with <paramref name="dryRun"/> sees what it would
    /// change (Enterprise). An applied rollover needs <paramref name="idempotencyKey"/>; a dry run changes
    /// nothing and takes none. Both are checked here.
    /// </summary>
    /// <returns>Each member's outcome, and where to continue.</returns>
    public Task<CopyGroupRolloverResult> RolloverGroupAsync(
        Guid groupId, bool dryRun, IEnumerable<CopyRolloverField>? fields = null, Guid? after = null, int? limit = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        // DECISION: the contract requires the key unless it is a dry run, and a dry run needs none.
        if (!dryRun && idempotencyKey is null)
        {
            throw new ArgumentException("an applied rollover (dryRun false) needs an idempotency key", nameof(idempotencyKey));
        }
        if (dryRun && idempotencyKey is not null)
        {
            throw new ArgumentException("a dry run changes nothing and takes no key", nameof(idempotencyKey));
        }
        var body = Body<CopyGroupRollover>("rollover_group", ("dry_run", dryRun), ("fields", Wire.Words(fields)?.ToList()),
            ("after", after), ("limit", limit));
        return _client.WriteAsync<CopyGroupRolloverResult>(HttpMethod.Post, $"/copy/groups/{Id(groupId)}/rollover", body,
            idempotencyKey is null ? null : Keys.Required(idempotencyKey), cancellationToken);
    }

    // --- requested closes, the log, signals and stop-outs -----------------------------------

    private static Query ClosesQuery(
        Guid? followerId, Guid? leaderId, Guid? groupId, Guid? batchId, IEnumerable<CopyCloseState>? state, DateTimeOffset? from, DateTimeOffset? to)
    {
        Window("the closes", from, to);
        return new Query().Add("follower_id", followerId).Add("leader_id", leaderId).Add("group_id", groupId).Add("batch_id", batchId)
            .Add("state", Joined(state, nameof(state))).Add("from", from).Add("to", to);
    }

    /// <summary>
    /// One page of the tenant's requested closes of copied positions, newest first, with a count of each
    /// state in <c>stats</c>; the window is on when the close was asked, at most 31 days.
    /// </summary>
    /// <returns>The page.</returns>
    public Task<CopyClosePage> ClosesAsync(
        string? cursor = null, int? limit = null, Guid? followerId = null, Guid? leaderId = null, Guid? groupId = null, Guid? batchId = null,
        IEnumerable<CopyCloseState>? state = null, DateTimeOffset? @from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyClosePage>("/copy/closes",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(ClosesQuery(followerId, leaderId, groupId, batchId, state, @from, to)),
            cancellationToken);

    /// <summary>Every requested close the filters and window match, following the cursor.</summary>
    /// <returns>Every close.</returns>
    public IAsyncEnumerable<CopyClose> AllClosesAsync(
        int? limit = null, Guid? followerId = null, Guid? leaderId = null, Guid? groupId = null, Guid? batchId = null,
        IEnumerable<CopyCloseState>? state = null, DateTimeOffset? @from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        var filters = ClosesQuery(followerId, leaderId, groupId, batchId, state, @from, to);
        return Pages.All((cursor, token) => _client.PageAsync<CopyClose>("/copy/closes",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(filters), token), cancellationToken);
    }

    /// <summary>One requested close: each copied position it covers and each attempt to close it.</summary>
    /// <returns>The close.</returns>
    public Task<CopyClose> GetCloseAsync(Guid closeId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyClose>($"/copy/closes/{Id(closeId)}", null, cancellationToken);

    private static Query LogQuery(
        Guid? leaderId, Guid? followerId, Guid? accountId, Guid? groupId, Guid? signalId, IEnumerable<CopyOutcome>? outcome,
        IEnumerable<CopyReason>? reason, IEnumerable<Venue>? venue, DateTimeOffset? from, DateTimeOffset? to, CopyLogSort? sort)
    {
        Window("the copy log", from, to);
        return new Query().Add("leader_id", leaderId).Add("follower_id", followerId).Add("account_id", accountId).Add("group_id", groupId)
            .Add("signal_id", signalId).Add("outcome", Joined(outcome, nameof(outcome))).Add("reason", Joined(reason, nameof(reason)))
            .Add("venue", Joined(venue, nameof(venue))).Add("from", from).Add("to", to).Add("sort", sort is { } s ? Wire.Word(s) : null);
    }

    /// <summary>
    /// One page of the copy log: one row a follower a signal, with its outcome, reason, the command it became
    /// and MFX Hub's stage timings. Newest first, or <see cref="CopyLogSort.MfxMs"/> for the slowest first.
    /// </summary>
    /// <returns>The page.</returns>
    public Task<CopyLogPage> LogAsync(
        string? cursor = null, int? limit = null, Guid? leaderId = null, Guid? followerId = null, Guid? accountId = null, Guid? groupId = null,
        Guid? signalId = null, IEnumerable<CopyOutcome>? outcome = null, IEnumerable<CopyReason>? reason = null, IEnumerable<Venue>? venue = null,
        DateTimeOffset? @from = null, DateTimeOffset? to = null, CopyLogSort? sort = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyLogPage>("/copy/log",
            new Query().Add("cursor", cursor).Add("limit", limit)
                .AddRange(LogQuery(leaderId, followerId, accountId, groupId, signalId, outcome, reason, venue, @from, to, sort)),
            cancellationToken);

    /// <summary>Every row of the copy log the filters and window match, following the cursor.</summary>
    /// <returns>Every row.</returns>
    public IAsyncEnumerable<CopyLogEntry> AllLogAsync(
        int? limit = null, Guid? leaderId = null, Guid? followerId = null, Guid? accountId = null, Guid? groupId = null, Guid? signalId = null,
        IEnumerable<CopyOutcome>? outcome = null, IEnumerable<CopyReason>? reason = null, IEnumerable<Venue>? venue = null,
        DateTimeOffset? @from = null, DateTimeOffset? to = null, CopyLogSort? sort = null, CancellationToken cancellationToken = default)
    {
        var filters = LogQuery(leaderId, followerId, accountId, groupId, signalId, outcome, reason, venue, @from, to, sort);
        return Pages.All((cursor, token) => _client.PageAsync<CopyLogEntry>("/copy/log",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(filters), token), cancellationToken);
    }

    private static Query SignalsQuery(
        Guid? leaderId, IEnumerable<CopySignalEntryKind>? kind, IEnumerable<CopySignalEntryAction>? action, DateTimeOffset? from, DateTimeOffset? to)
    {
        Window("the signals", from, to);
        return new Query().Add("from", from).Add("to", to).Add("leader_id", leaderId).Add("kind", Joined(kind, nameof(kind)))
            .Add("action", Joined(action, nameof(action)));
    }

    /// <summary>
    /// One page of the leaders' signals, newest first, each with how many of its copies were filled,
    /// refused and skipped. The window is at most 31 days; without a start it is the last 24 hours.
    /// </summary>
    /// <returns>The page.</returns>
    public Task<CopySignalPage> SignalsAsync(
        string? cursor = null, int? limit = null, Guid? leaderId = null, IEnumerable<CopySignalEntryKind>? kind = null,
        IEnumerable<CopySignalEntryAction>? action = null, DateTimeOffset? @from = null, DateTimeOffset? to = null,
        CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopySignalPage>("/copy/signals",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(SignalsQuery(leaderId, kind, action, @from, to)), cancellationToken);

    /// <summary>Every signal the filters and window match, following the cursor.</summary>
    /// <returns>Every signal.</returns>
    public IAsyncEnumerable<CopySignalEntry> AllSignalsAsync(
        int? limit = null, Guid? leaderId = null, IEnumerable<CopySignalEntryKind>? kind = null, IEnumerable<CopySignalEntryAction>? action = null,
        DateTimeOffset? @from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        var filters = SignalsQuery(leaderId, kind, action, @from, to);
        return Pages.All((cursor, token) => _client.PageAsync<CopySignalEntry>("/copy/signals",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(filters), token), cancellationToken);
    }

    private static Query TenantStopoutsQuery(
        IEnumerable<CopyStopoutState>? state, Guid? followerId, Guid? groupId, DateTimeOffset? from, DateTimeOffset? to)
    {
        Window("the stop-outs", from, to);
        return new Query().Add("from", from).Add("to", to).Add("state", Joined(state, nameof(state))).Add("follower_id", followerId).Add("group_id", groupId);
    }

    /// <summary>One page of every stop-out of the tenant's followers, newest first, with a count of each state in <c>stats</c>.</summary>
    /// <returns>The page.</returns>
    public Task<CopyTenantStopoutPage> TenantStopoutsAsync(
        string? cursor = null, int? limit = null, IEnumerable<CopyStopoutState>? state = null, Guid? followerId = null, Guid? groupId = null,
        DateTimeOffset? @from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CopyTenantStopoutPage>("/copy/stopouts",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(TenantStopoutsQuery(state, followerId, groupId, @from, to)), cancellationToken);

    /// <summary>Every stop-out the filters and window match, following the cursor.</summary>
    /// <returns>Every stop-out.</returns>
    public IAsyncEnumerable<CopyStopout> AllTenantStopoutsAsync(
        int? limit = null, IEnumerable<CopyStopoutState>? state = null, Guid? followerId = null, Guid? groupId = null,
        DateTimeOffset? @from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        var filters = TenantStopoutsQuery(state, followerId, groupId, @from, to);
        return Pages.All((cursor, token) => _client.PageAsync<CopyStopout>("/copy/stopouts",
            new Query().Add("cursor", cursor).Add("limit", limit).AddRange(filters), token), cancellationToken);
    }
}
