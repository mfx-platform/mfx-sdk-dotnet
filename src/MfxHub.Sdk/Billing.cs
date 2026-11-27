using MfxHub.Models;

namespace MfxHub;

/// <summary>
/// Billing reads (<c>client.Billing</c>; <c>billing:read</c>, and <see cref="PriceListAsync"/> needs no
/// scope). Never refused for billing: a suspended profile still reads its own standing. Amounts stay the
/// contract's exact decimal text.
/// </summary>
public sealed class Billing
{
    private readonly MfxClient _client;

    internal Billing(MfxClient client) => _client = client;

    /// <summary>
    /// The public price list: the plans, every item with its monthly price, and <c>next</c>, the following
    /// list once it is released. It needs no key; the SDK signs it as it signs every request, which the
    /// platform accepts and ignores.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The price list.</returns>
    public Task<BillingPriceList> PriceListAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<BillingPriceList>("/price-list", null, cancellationToken);

    /// <summary>
    /// The organisation's billing (<c>GET /billing</c>): every billing profile with its standing, and the
    /// most urgent standing at the top.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The summary.</returns>
    public Task<BillingSummary> SummaryAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<BillingSummary>("/billing", null, cancellationToken);

    /// <summary>One billing profile: its balance, today's daily charge, month to date and standing.</summary>
    /// <param name="profileId">The billing profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profile.</returns>
    public Task<BillingProfile> ProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<BillingProfile>("/billing/profiles/" + Wire.Id(profileId), null, cancellationToken);

    /// <summary>A month for one billing profile, to date by product and forecast to month end; a past month gives its final figures.</summary>
    /// <param name="profileId">The billing profile.</param>
    /// <param name="month">The month as <c>YYYY-MM</c>; this month when null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The forecast.</returns>
    public Task<BillingForecast> ForecastAsync(Guid profileId, string? month = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync<BillingForecast>("/billing/profiles/" + Wire.Id(profileId) + "/forecast", new Query().Add("month", month), cancellationToken);

    /// <summary>
    /// The prices the organisation pays, in the price list's shape: its Enterprise agreement's when one is
    /// in force, otherwise the list in force.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The prices.</returns>
    public Task<BillingPriceList> PricesAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<BillingPriceList>("/billing/prices", null, cancellationToken);

    /// <summary>One invoice, with its summary by product, tax, what is matched and how to pay.</summary>
    /// <param name="invoiceId">The invoice.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The invoice.</returns>
    public Task<BillingInvoiceDetail> InvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<BillingInvoiceDetail>("/billing/invoices/" + Wire.Id(invoiceId), null, cancellationToken);

    private static Query InvoicesQuery(Guid? billingProfileId, IEnumerable<BillingInvoiceState>? state) =>
        new Query().Add("billing_profile_id", billingProfileId).Add("state", WireNames.Joined(state, nameof(state)));

    /// <summary>
    /// One page of invoices, newest first, across the billing profiles: one profile's when named, in
    /// several states sent comma-separated and checked here. Up to 200 a page (50 when not said).
    /// </summary>
    /// <param name="billingProfileId">One profile's invoices only.</param>
    /// <param name="state">Only invoices in these states.</param>
    /// <param name="cursor">The cursor of the page before.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<BillingInvoicePage> InvoicesPageAsync(
        Guid? billingProfileId = null, IEnumerable<BillingInvoiceState>? state = null, string? cursor = null, int? limit = null,
        CancellationToken cancellationToken = default) =>
        _client.GetAsync<BillingInvoicePage>("/billing/invoices",
            InvoicesQuery(billingProfileId, state).Add("cursor", cursor).Add("limit", limit), cancellationToken);

    /// <summary>Every invoice, newest first, following the cursor; narrowed as <see cref="InvoicesPageAsync"/> is.</summary>
    /// <param name="billingProfileId">As <see cref="InvoicesPageAsync"/>.</param>
    /// <param name="state">As <see cref="InvoicesPageAsync"/>.</param>
    /// <param name="limit">The page size, not a total.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every invoice.</returns>
    public IAsyncEnumerable<BillingInvoice> InvoicesAsync(
        Guid? billingProfileId = null, IEnumerable<BillingInvoiceState>? state = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var filters = InvoicesQuery(billingProfileId, state);
        return Pages.All((cursor, token) => _client.PageAsync<BillingInvoice>("/billing/invoices",
            new Query().AddRange(filters).Add("cursor", cursor).Add("limit", limit), token), cancellationToken);
    }
}
