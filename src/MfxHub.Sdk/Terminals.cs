using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using MfxHub.Models;

namespace MfxHub;

/// <summary>
/// This tenant's terminals (<c>client.Terminals</c>): ask for one, submit the login that makes it
/// real, watch it reach ACTIVE, restart, drain, resume and retire it.
/// </summary>
/// <remarks>
/// Every write takes the caller's idempotency key and has no default: the point of the key is that
/// the caller can retry with the same one and get the same answer back rather than a second
/// terminal. A credential is a <see cref="Secret"/>, never printed.
/// </remarks>
public sealed class Terminals
{
    /// <summary>The states a terminal passes through on its way to ACTIVE; every other state is settled.</summary>
    public static readonly FrozenSet<string> OnItsWay =
        FrozenSet.ToFrozenSet(["REQUESTED", "QUEUED", "PROVISIONING", "LOGGING_IN"], StringComparer.Ordinal);

    /// <summary>An operation is over in any of these states; every other state is on its way.</summary>
    public static readonly FrozenSet<string> OperationFinal =
        FrozenSet.ToFrozenSet(["SUCCEEDED", "FAILED", "NOT_STARTED", "EXPIRED", "CANCELLED", "SUPERSEDED"], StringComparer.Ordinal);

    /// <summary>
    /// OFFLINE needs nothing from the caller: the platform brings the terminal back when its host
    /// reports again, or moves it to another host of its farm. Retrying a request against an OFFLINE
    /// terminal is right; re-creating the terminal is not.
    /// </summary>
    public const bool OfflineIsTemporary = true;

    private static readonly TimeSpan PollFrom = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollCap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QueuedCap = TimeSpan.FromSeconds(60);
    private const int BulkReasonMax = 500;
    private const int BulkIdsMax = 1000;

    private readonly MfxClient _client;

    internal Terminals(MfxClient client) => _client = client;

    /// <summary>
    /// Can this terminal be used right now, for market data, execution and account reads? True only in
    /// ACTIVE: every other state is not yet, not any more, not until resumed, or not at the moment
    /// (OFFLINE, FAILED). Checking through this means a state the platform adds later cannot read as usable.
    /// </summary>
    /// <param name="terminal">The terminal as read.</param>
    /// <returns>Whether it is ACTIVE.</returns>
    public static bool IsUsable(TenantTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return terminal.State == TenantTerminalState.Active;
    }

    private static string Path(Guid terminalId, string? rest = null) => "/terminals/" + Wire.Id(terminalId) + rest;

    /// <summary>
    /// Asks for a terminal. Answered at once in state <c>REQUESTED</c>: the move to a host is
    /// asynchronous, and the credential is submitted separately (<see cref="SubmitCredentialsAsync"/>).
    /// </summary>
    /// <param name="brokerId">The broker.</param>
    /// <param name="serverId">The broker's server.</param>
    /// <param name="idempotencyKey">The caller's key: the same key answers the same terminal.</param>
    /// <param name="farmId">
    /// The farm to place it on; when null the platform chooses (the tenant's own ACTIVE farms first,
    /// then the shared ones), and the farm it picked is on the answer. Nothing that fits is 409 <c>no_farm</c>.
    /// </param>
    /// <param name="streaming">Whether it streams prices.</param>
    /// <param name="execution">Whether it executes orders.</param>
    /// <param name="instruments">The broker's own symbol names (<c>EURUSD.sd</c>): MFX never renames an instrument.</param>
    /// <param name="labels">The terminal's labels.</param>
    /// <param name="redundancy">The terminal's redundancy; none by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The terminal, <c>REQUESTED</c>.</returns>
    public Task<TenantTerminal> CreateAsync(
        Guid brokerId,
        Guid serverId,
        string idempotencyKey,
        Guid? farmId = null,
        bool streaming = false,
        bool execution = false,
        IEnumerable<string>? instruments = null,
        IReadOnlyDictionary<string, string>? labels = null,
        TerminalCreateRedundancy redundancy = TerminalCreateRedundancy.None,
        CancellationToken cancellationToken = default)
    {
        var body = new TerminalCreate
        {
            FarmId = farmId,
            BrokerId = brokerId,
            ServerId = serverId,
            Streaming = streaming,
            Execution = execution,
            Instruments = instruments?.ToList(),
            Labels = labels?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Redundancy = Checked(redundancy),
        };
        return _client.WriteAsync<TenantTerminal>(HttpMethod.Post, "/terminals", body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>One terminal: its state, how long it has been in it, and its observed conditions.</summary>
    /// <param name="terminalId">The terminal's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The terminal.</returns>
    /// <exception cref="NotFoundException">There is no such terminal, or it is another tenant's: never a 403.</exception>
    public Task<TenantTerminal> GetAsync(Guid terminalId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<TenantTerminal>(Path(terminalId), null, cancellationToken);

    /// <summary>One page of this tenant's terminals, newest first, retired ones included.</summary>
    /// <param name="cursor">The <see cref="Page{T}.NextCursor"/> of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold; the platform's default when null.</param>
    /// <param name="label">
    /// Exact, case-sensitive label keys and values (at most 10): a terminal is listed only when it carries
    /// every one. Applied on the platform before the page is cut, so a filtered page is never short for
    /// being filtered.
    /// </param>
    /// <param name="state">
    /// The states to list, sent as one comma-separated value: a terminal is listed when it is in any of
    /// them. Each is checked here; none is no filter.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, with the cursor for the next; no total.</returns>
    public Task<Page<TenantTerminal>> PageAsync(
        string? cursor = null,
        int? limit = null,
        IReadOnlyDictionary<string, string>? label = null,
        IEnumerable<TenantTerminalState>? state = null,
        CancellationToken cancellationToken = default)
    {
        var wanted = WireNames.Joined(state, nameof(state)); // before the call: a bad one is the caller's
        var query = new Query().Add("cursor", cursor).Add("limit", limit);
        foreach (var (key, value) in label ?? new Dictionary<string, string>())
        {
            query.Add("label", key + ":" + value);
        }
        query.Add("state", wanted);
        return _client.PageAsync<TenantTerminal>("/terminals", query, cancellationToken);
    }

    /// <summary>
    /// Every terminal, following the cursor until it ends, narrowed as <see cref="PageAsync"/> is. There
    /// is no total and no page number anywhere in the API, so this follows the cursor rather than counting.
    /// </summary>
    /// <param name="limit">The page size to ask for; the platform's default when null.</param>
    /// <param name="label">As <see cref="PageAsync"/>.</param>
    /// <param name="state">As <see cref="PageAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the walk, between pages or during one.</param>
    /// <returns>Every terminal, page by page, one request a page.</returns>
    public IAsyncEnumerable<TenantTerminal> ListAsync(
        int? limit = null,
        IReadOnlyDictionary<string, string>? label = null,
        IEnumerable<TenantTerminalState>? state = null,
        CancellationToken cancellationToken = default)
    {
        var states = state?.ToList();
        return Pages.All((cursor, token) => PageAsync(cursor, limit, label, states, token), cancellationToken);
    }

    /// <summary>
    /// The terminal's MetaTrader journal, newest line first. While the terminal runs the lines are read
    /// from it now; once it has stopped they are the last lines the platform kept, for 24 hours.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="lines">1 to 200 lines (the platform's default is 100); outside that is refused here.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The journal.</returns>
    /// <exception cref="StateException">The terminal is not running and holds no last lines (<c>not_running</c>).</exception>
    public Task<TerminalJournal> JournalAsync(Guid terminalId, int? lines = null, CancellationToken cancellationToken = default)
    {
        if (lines is { } count && count is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(lines), lines, "lines must be 1 to 200");
        }
        return _client.GetAsync<TerminalJournal>(Path(terminalId, "/journal"), new Query().Add("lines", lines), cancellationToken);
    }

    /// <summary>
    /// Puts a broker login on a terminal: the only request in MFX Hub that carries a secret. It is
    /// written to Key Vault and never stored, logged or echoed; the answer is a reference. An
    /// <c>investor</c> credential turns execution off, and the answer says so.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="login">The broker login.</param>
    /// <param name="password">The password, as a <see cref="Secret"/> (a string converts to one).</param>
    /// <param name="server">The broker server's name, as a credential names it.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="credType">The kind of credential; master by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The accepted credential's reference.</returns>
    public Task<CredentialAccepted> SubmitCredentialsAsync(
        Guid terminalId,
        long login,
        Secret password,
        string server,
        string idempotencyKey,
        CredentialSubmissionCredType credType = CredentialSubmissionCredType.Master,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(server);
        var body = new CredentialBody(login, password.Reveal(), server, Wire.Word(credType));
        return _client.WriteAsync<CredentialAccepted>(
            HttpMethod.Post, Path(terminalId, "/credentials"), body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>
    /// Restarts a stuck terminal: the terminal is closed and started again on the same host. Answered 202 with
    /// the operation to follow (<see cref="WaitForOperationAsync"/>). A refusal names its rule in its code,
    /// with <c>Retry-After</c> when waiting is what helps.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="reason">Why, written for a person: operators read it in the activity feed.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="confirmOpenPositions">
    /// That the caller knows positions are open: a restart drops the broker session for a few seconds.
    /// </param>
    /// <param name="queue">
    /// True unless given: a restart refused only because the platform is busy is then queued and starts
    /// on its own. False keeps the refusal, as a 409 or 429 with <c>Retry-After</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The operation that was accepted.</returns>
    public Task<OperationAccepted> RestartAsync(
        Guid terminalId,
        string reason,
        string idempotencyKey,
        bool confirmOpenPositions = false,
        bool queue = true,
        CancellationToken cancellationToken = default)
    {
        var body = new TerminalRestart { Reason = reason, Queue = queue, ConfirmOpenPositions = confirmOpenPositions };
        return _client.WriteAsync<OperationAccepted>(
            HttpMethod.Post, Path(terminalId, "/restart"), body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>Replaces the terminal's whole instrument list, in the broker's own names.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="instruments">The whole list.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="depthInstruments">
    /// The symbols of <paramref name="instruments"/> whose order book it reads and streams: the whole
    /// list; empty stops depth; null keeps the depth symbols still in <paramref name="instruments"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The terminal.</returns>
    public Task<TenantTerminal> ReplaceInstrumentsAsync(
        Guid terminalId,
        IEnumerable<string> instruments,
        string idempotencyKey,
        IEnumerable<string>? depthInstruments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        var body = new TerminalInstruments { Instruments = [.. instruments], DepthInstruments = depthInstruments?.ToList() };
        return _client.WriteAsync<TenantTerminal>(
            HttpMethod.Put, Path(terminalId, "/instruments"), body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>
    /// Attaches an approved expert advisor to this terminal, on the symbol's chart and timeframe: a
    /// governed relaunch, answered 202 with the operation to follow. The expert then runs its trial
    /// with trading off; on a real-money account it trades only after <see cref="ConfirmExpertAdvisorAsync"/>.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="expertAdvisorId">The approved expert advisor.</param>
    /// <param name="symbol">The chart's symbol.</param>
    /// <param name="timeframe">The chart's timeframe.</param>
    /// <param name="reason">Why, written for a person.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="queue">Whether a relaunch refused only because the platform is busy is queued.</param>
    /// <param name="confirmOpenPositions">That the caller knows positions are open.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The operation that was accepted.</returns>
    public Task<OperationAccepted> AttachExpertAdvisorAsync(
        Guid terminalId,
        Guid expertAdvisorId,
        string symbol,
        ExpertAdvisorAttachTimeframe timeframe,
        string reason,
        string idempotencyKey,
        bool queue = false,
        bool confirmOpenPositions = false,
        CancellationToken cancellationToken = default)
    {
        var body = new ExpertAdvisorAttach
        {
            ExpertAdvisorId = expertAdvisorId,
            Symbol = symbol,
            Timeframe = Checked(timeframe),
            Reason = reason,
            Queue = queue,
            ConfirmOpenPositions = confirmOpenPositions,
        };
        return _client.WriteAsync<OperationAccepted>(
            HttpMethod.Put, Path(terminalId, "/expert-advisor"), body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>Takes the expert advisor off this terminal: a governed relaunch that rebuilds the folder, 202.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="reason">Why, written for a person.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="queue">Whether a relaunch refused only because the platform is busy is queued.</param>
    /// <param name="confirmOpenPositions">That the caller knows positions are open.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The operation that was accepted.</returns>
    public Task<OperationAccepted> DetachExpertAdvisorAsync(
        Guid terminalId,
        string reason,
        string idempotencyKey,
        bool queue = false,
        bool confirmOpenPositions = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var query = new Query().Add("reason", reason).Add("queue", queue).Add("confirm_open_positions", confirmOpenPositions);
        return _client.WriteAsync<OperationAccepted>(
            HttpMethod.Delete, Path(terminalId, "/expert-advisor"), null, Keys.Required(idempotencyKey), cancellationToken, query);
    }

    /// <summary>
    /// Turns on live trading on a real-money account after a green trial, with the one-time code sent to
    /// the account's representative. A wrong, used or expired code is 403 <c>confirmation_refused</c>,
    /// and the answer does not say which; five in a row lock it. It takes no idempotency key, as in the contract.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="code">The one-time code.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The attachment.</returns>
    public Task<ExpertAdvisorAttachment> ConfirmExpertAdvisorAsync(
        Guid terminalId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        return _client.WriteAsync<ExpertAdvisorAttachment>(
            HttpMethod.Post, Path(terminalId, "/expert-advisor/confirm"), new ExpertAdvisorConfirm { Code = code }, null, cancellationToken);
    }

    /// <summary>One operation on the terminal (restart, attach, detach, drain): its state, step and timeline.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="operationId">The operation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The operation.</returns>
    public Task<TerminalOperation> GetOperationAsync(Guid terminalId, Guid operationId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<TerminalOperation>(Path(terminalId, "/operations/" + Wire.Id(operationId)), null, cancellationToken);

    /// <summary>
    /// Reads the operation until it is over (<see cref="OperationFinal"/>) or <paramref name="timeout"/>
    /// has passed on the client's clock, 2 s between reads, doubling to 15 s. Returns the last read
    /// either way: an operation still running when the time runs out is reported as it is.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="operationId">The operation.</param>
    /// <param name="timeout">How long to wait; 900 s when null.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The operation as last read.</returns>
    public async Task<TerminalOperation> WaitForOperationAsync(
        Guid terminalId, Guid operationId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var clock = _client.Clock;
        var started = clock.GetTimestamp();
        var limit = timeout ?? TimeSpan.FromSeconds(900);
        var wait = PollFrom;
        var operation = await GetOperationAsync(terminalId, operationId, cancellationToken).ConfigureAwait(false);
        while (!OperationFinal.Contains(Wire.Word(operation.State)) && clock.GetElapsedTime(started) < limit)
        {
            await _client.PauseAsync(wait < PollCap ? wait : PollCap, cancellationToken).ConfigureAwait(false);
            wait *= 2;
            operation = await GetOperationAsync(terminalId, operationId, cancellationToken).ConfigureAwait(false);
        }
        return operation;
    }

    /// <summary>
    /// Reads the terminal until it is no longer on its way to ACTIVE (<see cref="OnItsWay"/>), or until
    /// <paramref name="timeout"/> has been waited. Returns the last read either way: ACTIVE, a state it
    /// settled in instead (FAILED, for one), or still on its way when the time ran out.
    /// </summary>
    /// <remarks>
    /// A QUEUED terminal is read again at its <c>queue.expected_start_at</c> (between 2 s and a minute
    /// away), not on a fixed beat. Any other state is read again after 2 s, doubling to 15 s.
    /// </remarks>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="timeout">How long to wait, zero or more; 900 s when null.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The terminal as last read.</returns>
    public async Task<TenantTerminal> WaitUntilActiveAsync(
        Guid terminalId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(900);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, TimeSpan.Zero, nameof(timeout));
        var waited = TimeSpan.Zero;
        var backoff = PollFrom;
        var terminal = await GetAsync(terminalId, cancellationToken).ConfigureAwait(false);
        while (OnItsWay.Contains(Wire.Word(terminal.State)) && waited < limit)
        {
            var next = NextWait(terminal, backoff, _client.Clock.GetUtcNow());
            var seconds = next < limit - waited ? next : limit - waited;
            await _client.PauseAsync(seconds, cancellationToken).ConfigureAwait(false);
            waited += seconds;
            backoff *= 2;
            terminal = await GetAsync(terminalId, cancellationToken).ConfigureAwait(false);
        }
        return terminal;
    }

    /// <summary>
    /// How long to wait before the next read of a terminal on its way to ACTIVE: a QUEUED terminal with
    /// an expected start is read then, between 2 s and a minute away; anything else the backoff, at most 15 s.
    /// </summary>
    internal static TimeSpan NextWait(TenantTerminal terminal, TimeSpan backoff, DateTimeOffset now)
    {
        if (terminal.State == TenantTerminalState.Queued && terminal.Queue?.ExpectedStartAt is { } expected)
        {
            var until = expected - now;
            return until < PollFrom ? PollFrom : until > QueuedCap ? QueuedCap : until;
        }
        return backoff < PollCap ? backoff : PollCap;
    }

    /// <summary>
    /// Restarts many terminals at once: those on <paramref name="farmId"/>, at <paramref name="brokerId"/>,
    /// or listed in <paramref name="terminalIds"/> (at most 1000), and at least one of the three. Each is
    /// judged by the rules of a single restart; a refused one is listed in <c>refused</c> with its rule's
    /// code, and the rest are taken. Answered at once with the action and its expected finish.
    /// </summary>
    /// <param name="reason">Why, 1 to 500 characters, written for a person.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="farmId">The farm whose terminals to restart.</param>
    /// <param name="brokerId">The broker whose terminals to restart.</param>
    /// <param name="terminalIds">The terminals to restart, at most 1000.</param>
    /// <param name="confirmOpenPositions">
    /// That the caller knows positions are open; without it, a terminal with positions open is refused
    /// (<c>positions_open</c>) and the rest go ahead.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bulk action.</returns>
    public Task<BulkAction> BulkRestartAsync(
        string reason,
        string idempotencyKey,
        Guid? farmId = null,
        Guid? brokerId = null,
        IEnumerable<Guid>? terminalIds = null,
        bool confirmOpenPositions = false,
        CancellationToken cancellationToken = default)
    {
        var body = BulkAsk(reason, farmId, brokerId, terminalIds?.ToList(), confirmOpenPositions);
        return _client.WriteAsync<BulkAction>(HttpMethod.Post, "/terminals:restart", body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>The body of a bulk action, checked here so a bad one is the caller's error, not a 422.</summary>
    internal static BulkTerminalAsk BulkAsk(
        string reason, Guid? farmId, Guid? brokerId, IReadOnlyCollection<Guid>? terminalIds, bool confirmOpenPositions)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > BulkReasonMax)
        {
            throw new ArgumentException($"reason is 1 to {BulkReasonMax} characters", nameof(reason));
        }
        if (farmId is null && brokerId is null && terminalIds is null)
        {
            throw new ArgumentException("a bulk action names farmId, brokerId or terminalIds", nameof(terminalIds));
        }
        if (terminalIds is { Count: > BulkIdsMax })
        {
            throw new ArgumentException($"terminalIds is at most {BulkIdsMax}", nameof(terminalIds));
        }
        return new BulkTerminalAsk
        {
            Reason = reason,
            FarmId = farmId,
            BrokerId = brokerId,
            TerminalIds = terminalIds?.ToList(),
            ConfirmOpenPositions = confirmOpenPositions,
        };
    }

    /// <summary>A bulk action's progress: counts per state, what was refused, and its expected finish.</summary>
    /// <param name="bulkActionId">The bulk action.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bulk action.</returns>
    public Task<BulkAction> GetBulkActionAsync(Guid bulkActionId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<BulkAction>("/bulk-actions/" + Wire.Id(bulkActionId), null, cancellationToken);

    /// <summary>Cancels a bulk action: its items still QUEUED settle CANCELLED, running ones finish.</summary>
    /// <param name="bulkActionId">The bulk action.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bulk action.</returns>
    public Task<BulkAction> CancelBulkActionAsync(Guid bulkActionId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _client.WriteAsync<BulkAction>(
            HttpMethod.Post, "/bulk-actions/" + Wire.Id(bulkActionId) + "/cancel", null, Keys.Required(idempotencyKey), cancellationToken);

    /// <summary>
    /// Closes the terminal gracefully and keeps it: DRAINED holds no capacity and is not billed. Answered
    /// with the terminal DRAINING; it keeps its id, account, credential, instruments and labels and runs
    /// nothing until <see cref="ResumeAsync"/>.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="reason">Why, for the audit log; recorded as not given when null.</param>
    /// <param name="confirmOpenPositions">Required true when the account has positions open (409 <c>positions_open</c>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The terminal, DRAINING.</returns>
    public Task<TenantTerminal> DrainAsync(
        Guid terminalId,
        string idempotencyKey,
        string? reason = null,
        bool confirmOpenPositions = false,
        CancellationToken cancellationToken = default)
    {
        var body = new TerminalDrain { Reason = reason, ConfirmOpenPositions = confirmOpenPositions };
        return _client.WriteAsync<TenantTerminal>(
            HttpMethod.Post, Path(terminalId, "/drain"), body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>
    /// Places a DRAINED terminal again: back to REQUESTED, then placed and logged in as new. A terminal
    /// already on its way back is answered as it is, so a resume sent twice is one resume.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The terminal.</returns>
    public Task<TenantTerminal> ResumeAsync(Guid terminalId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _client.WriteAsync<TenantTerminal>(HttpMethod.Post, Path(terminalId, "/resume"), null, Keys.Required(idempotencyKey), cancellationToken);

    /// <summary>Retires a terminal: it drains, then retires. Answered with its state now (DRAINING).</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The terminal.</returns>
    public Task<TenantTerminal> RetireAsync(Guid terminalId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _client.WriteAsync<TenantTerminal>(HttpMethod.Delete, Path(terminalId), null, Keys.Required(idempotencyKey), cancellationToken);

    private static TEnum Checked<TEnum>(TEnum value, [CallerArgumentExpression(nameof(value))] string parameter = "")
        where TEnum : struct, Enum
    {
        _ = WireNames.Of(value, parameter);
        return value;
    }

    /// <summary>
    /// A credential as the API reads it. The login is a <see cref="long"/> here, where the generated
    /// model has an <see cref="int"/>, because a terminal login can be larger than an <see cref="int"/>.
    /// </summary>
    private sealed record CredentialBody(
        [property: JsonPropertyName("login")] long Login,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("server")] string Server,
        [property: JsonPropertyName("cred_type")] string CredType);
}
