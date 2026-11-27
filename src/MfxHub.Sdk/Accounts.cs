using MfxHub.Models;

namespace MfxHub;

/// <summary>Balance, positions, orders, history and spreads for one tenant's terminals (<c>client.Accounts</c>).</summary>
public sealed class Accounts
{
    private readonly MfxClient _client;

    internal Accounts(MfxClient client) => _client = client;

    private static string Path(Guid terminalId, string rest) => "/terminals/" + Wire.Id(terminalId) + rest;

    /// <summary>
    /// Balance, equity and margin, plus the slow profile facts, each half with its own <c>ts</c>; a half
    /// never observed is null, not zero.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account.</returns>
    public Task<Account> AccountAsync(Guid terminalId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<Account>(Path(terminalId, "/account"), null, cancellationToken);

    /// <summary>Every open position, as one snapshot with its <c>as_of</c>.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The positions.</returns>
    public Task<PositionList> PositionsAsync(Guid terminalId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<PositionList>(Path(terminalId, "/positions"), null, cancellationToken);

    /// <summary>Every pending order, as one snapshot with its <c>as_of</c>.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pending orders.</returns>
    public Task<PendingOrderList> PendingOrdersAsync(Guid terminalId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<PendingOrderList>(Path(terminalId, "/orders/pending"), null, cancellationToken);

    /// <summary>
    /// One page of closed deals, newest first, inside a window. The window is required, by the API and
    /// so by this: an unbounded history request is the query that takes a database down.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="from">The window's start.</param>
    /// <param name="to">The window's end.</param>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<Deal>> DealsAsync(
        Guid terminalId, DateTimeOffset @from, DateTimeOffset to, string? cursor = null, int? limit = null,
        CancellationToken cancellationToken = default) =>
        _client.PageAsync<Deal>(
            Path(terminalId, "/history/deals"),
            new Query().Add("from", @from).Add("to", to).Add("cursor", cursor).Add("limit", limit),
            cancellationToken);

    /// <summary>
    /// The spread this terminal's own account is quoted, with each reading's age: the spread a user trades
    /// against is the one their broker gives their account, so it is read from their terminal.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The spreads.</returns>
    public Task<SpreadList> SpreadAsync(Guid terminalId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<SpreadList>(Path(terminalId, "/spread"), null, cancellationToken);
}
