using MfxHub.Models;

namespace MfxHub;

/// <summary>The farms the plan allows (<c>client.Farms</c>). Any other farm, another tenant's included, is 404.</summary>
public sealed class Farms
{
    private readonly MfxClient _client;

    internal Farms(MfxClient client) => _client = client;

    /// <summary>One page of farms.</summary>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<TenantFarm>> PageAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.PageAsync<TenantFarm>("/farms", new Query().Add("cursor", cursor).Add("limit", limit), cancellationToken);

    /// <summary>Every farm, following the cursor.</summary>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every farm, one request a page.</returns>
    public IAsyncEnumerable<TenantFarm> ListAsync(int? limit = null, CancellationToken cancellationToken = default) =>
        Pages.All((cursor, token) => PageAsync(cursor, limit, token), cancellationToken);

    /// <summary>One farm the plan allows.</summary>
    /// <param name="farmId">The farm.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The farm.</returns>
    public Task<TenantFarm> GetAsync(Guid farmId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<TenantFarm>("/farms/" + Wire.Id(farmId), null, cancellationToken);
}

/// <summary>What has been used and what is left (<c>client.Usage</c>): one call, because there is one question.</summary>
public sealed class UsageReader
{
    private readonly MfxClient _client;

    internal UsageReader(MfxClient client) => _client = client;

    /// <summary>Every limit for this key and its tenant: figure, source, whether applied, and use.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The usage.</returns>
    public Task<Usage> GetAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<Usage>("/usage", null, cancellationToken);
}
