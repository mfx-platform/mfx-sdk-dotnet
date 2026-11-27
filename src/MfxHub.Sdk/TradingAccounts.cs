using MfxHub.Models;

namespace MfxHub;

/// <summary>
/// The trading accounts this tenant holds, over every venue: a terminal account or a cTrader one
/// (<c>client.TradingAccounts</c>). The account's reads, its commands (the same bodies and rules as
/// <see cref="Execution"/>, sent to <c>/accounts/{id}/...</c>, each returning an
/// <see cref="ExecutionResult"/> and never throwing for an outcome) and the cTrader grant flow.
/// </summary>
public sealed class TradingAccounts
{
    private readonly MfxClient _client;
    private readonly Commands _orders;

    internal TradingAccounts(MfxClient client)
    {
        _client = client;
        _orders = new Commands(client, "accounts");
    }

    private static string Path(Guid accountId, string rest = "") => "/accounts/" + Wire.Id(accountId) + rest;

    private static string Grant(Guid grantId, string rest = "") => "/accounts/ctrader/grants/" + Wire.Id(grantId) + rest;

    /// <summary>One trading account, by its own id or by one of its terminals' ids. Another tenant's is a 404, never a 403.</summary>
    /// <param name="accountId">The account, or one of its terminals.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account.</returns>
    public Task<TradingAccount> GetAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<TradingAccount>(Path(accountId), null, cancellationToken);

    /// <summary>
    /// One page of this tenant's trading accounts, newest first, retired ones included. Each filter
    /// narrows the list on the platform before the page is cut; several venues or states go as one
    /// comma-separated value, each checked here.
    /// </summary>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="venue">The venues to list.</param>
    /// <param name="environment">Live or demo.</param>
    /// <param name="state">The states to list.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<TradingAccount>> PageAsync(
        string? cursor = null,
        int? limit = null,
        IEnumerable<TradingAccountVenue>? venue = null,
        TradingAccountEnvironment? environment = null,
        IEnumerable<TradingAccountState>? state = null,
        CancellationToken cancellationToken = default)
    {
        var venues = WireNames.Joined(venue, nameof(venue));
        var states = WireNames.Joined(state, nameof(state));
        var environments = environment is { } env ? Wire.Word(env) : null;
        return _client.PageAsync<TradingAccount>("/accounts",
            new Query().Add("cursor", cursor).Add("limit", limit).Add("venue", venues).Add("environment", environments).Add("state", states),
            cancellationToken);
    }

    /// <summary>Every trading account, following the cursor, narrowed as <see cref="PageAsync"/> is.</summary>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="venue">As <see cref="PageAsync"/>.</param>
    /// <param name="environment">As <see cref="PageAsync"/>.</param>
    /// <param name="state">As <see cref="PageAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every account, one request a page.</returns>
    public IAsyncEnumerable<TradingAccount> ListAsync(
        int? limit = null,
        IEnumerable<TradingAccountVenue>? venue = null,
        TradingAccountEnvironment? environment = null,
        IEnumerable<TradingAccountState>? state = null,
        CancellationToken cancellationToken = default)
    {
        var venues = venue?.ToList();
        var states = state?.ToList();
        return Pages.All((cursor, token) => PageAsync(cursor, limit, venues, environment, states, token), cancellationToken);
    }

    /// <summary>
    /// Balance, equity, margin and profile, answered by the account's lead runner. On cTrader the equity
    /// is computed by the platform and <c>stale</c> says when it could not be computed recently.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account's funds.</returns>
    public Task<Account> FundsAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<Account>(Path(accountId, "/account"), null, cancellationToken);

    /// <summary>Every open position, as one snapshot with its <c>as_of</c>. On cTrader the volume is in lots and the ticket is the venue's position id.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The positions.</returns>
    public Task<PositionList> PositionsAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<PositionList>(Path(accountId, "/positions"), null, cancellationToken);

    /// <summary>Every pending order, as one snapshot with its <c>as_of</c>.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pending orders.</returns>
    public Task<PendingOrderList> PendingOrdersAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<PendingOrderList>(Path(accountId, "/orders/pending"), null, cancellationToken);

    /// <summary>
    /// One page of deals, newest first, inside a window of at most 31 days, optionally of one position.
    /// Read from the venue when asked: a venue that does not answer in time is an <see cref="UpstreamException"/>.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="from">The window's start.</param>
    /// <param name="to">The window's end.</param>
    /// <param name="position">One position's deals only.</param>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<Deal>> DealsAsync(
        Guid accountId, DateTimeOffset @from, DateTimeOffset to, long? position = null, string? cursor = null, int? limit = null,
        CancellationToken cancellationToken = default) =>
        _client.PageAsync<Deal>(Path(accountId, "/history/deals"),
            new Query().Add("from", @from).Add("to", to).Add("position", position).Add("cursor", cursor).Add("limit", limit),
            cancellationToken);

    /// <summary>
    /// The symbols a cTrader account can trade, with their volume rules in lots and leverage. A terminal
    /// account is refused (its symbols are its broker's catalogue).
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The symbols.</returns>
    public Task<TradingSymbolList> SymbolsAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<TradingSymbolList>(Path(accountId, "/symbols"), null, cancellationToken);

    /// <summary>
    /// Places a market or pending order on the account, as <see cref="Execution.PlaceOrderAsync"/> does.
    /// The volume is in lots on every venue, and on cTrader a whole step of the symbol (<see cref="SymbolsAsync"/>).
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="symbol">The instrument.</param>
    /// <param name="side">Buy or sell.</param>
    /// <param name="volume">The volume in lots.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="orderType">Market by default.</param>
    /// <param name="price">The pending order's price.</param>
    /// <param name="stopLimitPrice">A stop-limit order's limit price.</param>
    /// <param name="sl">The stop loss.</param>
    /// <param name="tp">The take profit.</param>
    /// <param name="deviationPoints">The deviation allowed, in points.</param>
    /// <param name="expiresAt">When a pending order expires.</param>
    /// <param name="magic">The order's magic number; on cTrader its label, back on the deals.</param>
    /// <param name="comment">
    /// The order's comment, at most 31 characters counted as Unicode code points (the contract's maxLength): the terminal
    /// cuts a longer one without saying so, so a longer one throws <see cref="ArgumentException"/> here, before
    /// anything is sent.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> PlaceOrderAsync(
        Guid accountId, string symbol, Side side, decimal volume, string idempotencyKey, OrderType orderType = OrderType.Market,
        decimal? price = null, decimal? stopLimitPrice = null, decimal? sl = null, decimal? tp = null, int? deviationPoints = null,
        DateTimeOffset? expiresAt = null, int? magic = null, string? comment = null, CancellationToken cancellationToken = default) =>
        _orders.PlaceOrderAsync(accountId, symbol, side, volume, idempotencyKey, orderType, price, stopLimitPrice, sl, tp,
            deviationPoints, expiresAt, magic, comment, cancellationToken);

    /// <summary>Cancels a pending order, as <see cref="Execution.CancelOrderAsync"/>.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="ticket">The order.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> CancelOrderAsync(Guid accountId, long ticket, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _orders.CancelOrderAsync(accountId, ticket, idempotencyKey, cancellationToken);

    /// <summary>Amends a pending order, as <see cref="Execution.ModifyOrderAsync"/>.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="ticket">The order.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="price">The new price.</param>
    /// <param name="stopLimitPrice">The new limit price.</param>
    /// <param name="sl">The new stop loss.</param>
    /// <param name="tp">The new take profit.</param>
    /// <param name="expiresAt">The expiry: kept when null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ModifyOrderAsync(
        Guid accountId, long ticket, string idempotencyKey, decimal? price = null, decimal? stopLimitPrice = null,
        decimal? sl = null, decimal? tp = null, Expiry? expiresAt = null, CancellationToken cancellationToken = default) =>
        _orders.ModifyOrderAsync(accountId, ticket, idempotencyKey, price, stopLimitPrice, sl, tp, expiresAt, cancellationToken);

    /// <summary>Moves a position's stop loss, take profit or both, as <see cref="Execution.ModifyPositionAsync"/>.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="ticket">The position.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="sl">The new stop loss.</param>
    /// <param name="tp">The new take profit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ModifyPositionAsync(
        Guid accountId, long ticket, string idempotencyKey, decimal? sl = null, decimal? tp = null, CancellationToken cancellationToken = default) =>
        _orders.ModifyPositionAsync(accountId, ticket, idempotencyKey, sl, tp, cancellationToken);

    /// <summary>Closes a position in full, as <see cref="Execution.ClosePositionAsync"/>.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="ticket">The position.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="deviationPoints">The deviation allowed, in points.</param>
    /// <param name="comment">
    /// Written on the closing deal (<c>Deal.comment</c>), so a caller can tell its own closes apart; without one the
    /// deal takes the position's own comment and none is sent. At most 31 characters counted as Unicode code points
    /// (the contract's maxLength): the terminal cuts a longer one without saying so, so a longer one throws
    /// <see cref="ArgumentException"/> here, before anything is sent.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ClosePositionAsync(
        Guid accountId, long ticket, string idempotencyKey, int? deviationPoints = null, string? comment = null,
        CancellationToken cancellationToken = default) =>
        _orders.ClosePositionAsync(accountId, ticket, idempotencyKey, deviationPoints, comment, cancellationToken);

    /// <summary>
    /// Closes a position against an opposite one, as <see cref="Execution.ClosePositionByAsync"/>. cTrader
    /// has no close-by: it is done as two closes, and the result says <c>CloseByEmulated</c> with both deals.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="ticket">The position being closed.</param>
    /// <param name="byTicket">The opposite position.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ClosePositionByAsync(
        Guid accountId, long ticket, long byTicket, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _orders.ClosePositionByAsync(accountId, ticket, byTicket, idempotencyKey, cancellationToken);

    /// <summary>Closes part of a position, in lots, as <see cref="Execution.ClosePositionPartialAsync"/>.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="ticket">The position.</param>
    /// <param name="volume">The volume to close, in lots.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="deviationPoints">The deviation allowed, in points.</param>
    /// <param name="comment">
    /// Written on the closing deal (<c>Deal.comment</c>), so a caller can tell its own closes apart; without one the
    /// deal takes the position's own comment and none is sent. At most 31 characters counted as Unicode code points
    /// (the contract's maxLength): the terminal cuts a longer one without saying so, so a longer one throws
    /// <see cref="ArgumentException"/> here, before anything is sent.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ClosePositionPartialAsync(
        Guid accountId, long ticket, decimal volume, string idempotencyKey, int? deviationPoints = null, string? comment = null,
        CancellationToken cancellationToken = default) =>
        _orders.ClosePositionPartialAsync(accountId, ticket, volume, idempotencyKey, deviationPoints, comment, cancellationToken);

    /// <summary>Trails a position's stop loss, as <see cref="Execution.SetTrailingStopAsync"/>; a venue without one refuses it, as a result.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="ticket">The position.</param>
    /// <param name="distance">The price difference to trail by.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> SetTrailingStopAsync(
        Guid accountId, long ticket, decimal distance, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _orders.SetTrailingStopAsync(accountId, ticket, distance, idempotencyKey, cancellationToken);

    /// <summary>Removes a position's trailing stop, as <see cref="Execution.ClearTrailingStopAsync"/>.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="ticket">The position.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ExecutionResult> ClearTrailingStopAsync(Guid accountId, long ticket, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _orders.ClearTrailingStopAsync(accountId, ticket, idempotencyKey, cancellationToken);

    /// <summary>
    /// Starts connecting a trader's cTrader accounts; answers the consent link to send them to. The trader
    /// signs in at cTrader (MFX Hub never sees the password), chooses the accounts to share, and comes back
    /// to <paramref name="returnUrl"/> with the grant's id, the result and <paramref name="tenantState"/>.
    /// The link lives 15 minutes.
    /// </summary>
    /// <param name="environment">Live or demo accounts.</param>
    /// <param name="scope">Accounts only, or trading.</param>
    /// <param name="returnUrl">Where the trader comes back to (https).</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="tenantState">The caller's own value, handed back on the return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant and its consent link.</returns>
    /// <exception cref="ForbiddenException">The plan has no cTrader.</exception>
    public Task<CtraderGrantStarted> StartCtraderGrantAsync(
        CtraderGrantCreateEnvironment environment, CtraderGrantCreateScope scope, string returnUrl, string idempotencyKey,
        string? tenantState = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(returnUrl);
        // The body's model takes a URI; one that is not an absolute web address cannot be written into it.
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var back) || (back.Scheme != Uri.UriSchemeHttps && back.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("the return URL is an absolute https URL", nameof(returnUrl));
        }
        _ = Wire.Word(environment);
        _ = Wire.Word(scope);
        var body = new CtraderGrantCreate
        {
            Environment = environment,
            Scope = scope,
            ReturnUrl = back,
            TenantState = tenantState,
        };
        return _client.WriteAsync<CtraderGrantStarted>(HttpMethod.Post, "/accounts/ctrader/grants", body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>One grant: its state and, once ACTIVE, the cTrader accounts the trader shared.</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant.</returns>
    public Task<CtraderGrant> CtraderGrantAsync(Guid grantId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<CtraderGrant>(Grant(grantId), null, cancellationToken);

    /// <summary>
    /// Makes some of an ACTIVE grant's accounts trading accounts of this tenant, by cTrader's ids (1 to
    /// 50). Each starts PENDING and is CONNECTED once authorised; one already connected comes back unchanged.
    /// </summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="ctidTraderAccountIds">cTrader's account ids; at least one.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The trading accounts.</returns>
    public async Task<IReadOnlyList<TradingAccount>> ConnectCtraderAccountsAsync(
        Guid grantId, IEnumerable<string> ctidTraderAccountIds, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctidTraderAccountIds);
        var ids = ctidTraderAccountIds.ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("connecting cTrader accounts needs at least one account id", nameof(ctidTraderAccountIds));
        }
        var connected = await _client.WriteAsync<WirePage<TradingAccount>>(
            HttpMethod.Post, Grant(grantId, "/accounts"), new CtraderAccountsChoice { CtidTraderAccountIds = ids },
            Keys.Required(idempotencyKey), cancellationToken).ConfigureAwait(false);
        return [.. connected.Items ?? []];
    }

    /// <summary>
    /// Revokes a grant: its accounts move to REAUTH_NEEDED and a new grant reconnects them. Revoking a
    /// revoked grant answers it unchanged.
    /// </summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant.</returns>
    public Task<CtraderGrant> RevokeCtraderGrantAsync(Guid grantId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _client.WriteAsync<CtraderGrant>(HttpMethod.Delete, Grant(grantId), null, Keys.Required(idempotencyKey), cancellationToken);
}
