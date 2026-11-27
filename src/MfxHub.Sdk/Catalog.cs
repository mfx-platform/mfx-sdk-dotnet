using MfxHub.Models;

namespace MfxHub;

/// <summary>The brokers, servers and symbols this tenant may use (<c>client.Catalog</c>).</summary>
/// <remarks>
/// Symbols are the broker's own names: MFX Hub never renames an instrument on a tenant's behalf, so
/// <c>BTCUSD.lv</c> is what a broker that calls it that will answer to.
/// </remarks>
public sealed class Catalog
{
    private readonly MfxClient _client;

    internal Catalog(MfxClient client) => _client = client;

    private static string Broker(Guid brokerId, string rest = "") => "/brokers/" + Wire.Id(brokerId) + rest;

    /// <summary>
    /// One page of the brokers MFX Hub supports, with each one's status: <c>active</c> is usable,
    /// <c>suspended</c> and <c>retired</c> are not. A terminal asked for on a broker that is not usable
    /// is refused with <see cref="UnsupportedBrokerException"/>.
    /// </summary>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold; the platform's default when null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<TenantBroker>> BrokersAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.PageAsync<TenantBroker>("/brokers", new Query().Add("cursor", cursor).Add("limit", limit), cancellationToken);

    /// <summary>Every broker, following the cursor.</summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every broker, one request a page.</returns>
    public IAsyncEnumerable<TenantBroker> AllBrokersAsync(CancellationToken cancellationToken = default) =>
        Pages.All((cursor, token) => BrokersAsync(cursor, null, token), cancellationToken);

    /// <summary>
    /// One page of the trading servers this broker runs, and whether MFX Hub can use each one now.
    /// <c>disabled_reason</c> is the operator's sentence, the same words a refusal carries, so a caller
    /// can show why before it asks for a terminal.
    /// </summary>
    /// <param name="brokerId">The broker.</param>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<TenantBrokerServer>> ServersAsync(
        Guid brokerId, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.PageAsync<TenantBrokerServer>(
            Broker(brokerId, "/servers"), new Query().Add("cursor", cursor).Add("limit", limit), cancellationToken);

    /// <summary>
    /// One row per server and account type the tenant holds a symbol catalogue for on this broker. Each
    /// carries <c>catalogue_hash</c>, a digest of the whole symbol list that never changes on a quote
    /// figure: compare it with the last one and read <see cref="SymbolsAsync"/> again only when it differs.
    /// A broker with no catalogue answers an empty list.
    /// </summary>
    /// <param name="brokerId">The broker.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The catalogues.</returns>
    public async Task<IReadOnlyList<BrokerCatalogue>> CataloguesAsync(Guid brokerId, CancellationToken cancellationToken = default)
    {
        var list = await _client.GetAsync<BrokerCatalogueList>(Broker(brokerId, "/catalogues"), null, cancellationToken).ConfigureAwait(false);
        return [.. list.Items ?? []];
    }

    /// <summary>
    /// One page of the symbols this broker lists, in its own names: the names to pass to
    /// <see cref="Terminals.CreateAsync"/> and to place an order with. The page's
    /// <see cref="Page{T}.Source"/> says where the rows came from: <c>live</c> when a terminal of the
    /// tenant runs on this server and account type, <c>last_known</c> when none does any more.
    /// </summary>
    /// <param name="brokerId">The broker.</param>
    /// <param name="serverId">Narrows the list to one server, for a tenant with more than one on this broker.</param>
    /// <param name="accountType">Narrows the list to one account type.</param>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<Symbol>> SymbolsAsync(
        Guid brokerId,
        Guid? serverId = null,
        string? accountType = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        _client.PageAsync<Symbol>(
            Broker(brokerId, "/symbols"),
            Which(serverId, accountType).Add("cursor", cursor).Add("limit", limit),
            cancellationToken);

    /// <summary>
    /// One symbol's specification, including <c>pip_value_per_lot</c>. A symbol listed on more than
    /// one of the tenant's servers or account types is 409 <c>ambiguous_symbol</c>; name
    /// <paramref name="serverId"/> and <paramref name="accountType"/> to say which.
    /// </summary>
    /// <param name="brokerId">The broker.</param>
    /// <param name="symbol">The broker's name for it.</param>
    /// <param name="serverId">Which server.</param>
    /// <param name="accountType">Which account type.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The symbol.</returns>
    public Task<Symbol> SymbolAsync(
        Guid brokerId, string symbol, Guid? serverId = null, string? accountType = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<Symbol>(Broker(brokerId, "/symbols/" + Wire.Segment(symbol)), Which(serverId, accountType), cancellationToken);

    /// <summary>
    /// The current quote for one symbol, with its age: a quote is never returned without saying how old
    /// it is, because a stale quote reads exactly like a live one and gets acted on. Read from one of the
    /// tenant's terminals on that broker's server. <c>state</c> says whether the market can be traded;
    /// <c>broker_time</c> is the broker's own clock, which is not UTC. Prices stay exact decimal text.
    /// </summary>
    /// <param name="brokerId">The broker.</param>
    /// <param name="symbol">The broker's name for it.</param>
    /// <param name="serverId">Which server.</param>
    /// <param name="accountType">Which account type.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The quote.</returns>
    public Task<SymbolQuote> QuoteAsync(
        Guid brokerId, string symbol, Guid? serverId = null, string? accountType = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<SymbolQuote>(
            Broker(brokerId, "/symbols/" + Wire.Segment(symbol) + "/quote"), Which(serverId, accountType), cancellationToken);

    /// <summary>The two parameters that say which of a tenant's servers and account types is meant; what is not named is left out.</summary>
    private static Query Which(Guid? serverId, string? accountType) =>
        new Query().Add("server_id", serverId).Add("account_type", accountType);
}
