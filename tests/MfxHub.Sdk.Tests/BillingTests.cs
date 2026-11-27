using System.Reflection;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>The payment-required errors: PaymentRequiredException and BillingPaymentRequiredException.</summary>
public sealed class PaymentRequiredTests
{
    internal const string Refusal = """
        {"error": {"code": "billing_payment_required", "message": "Top up your balance to keep trading",
         "details": {"tenant_id": "t-1", "billing_profile_id": "bp-1", "stage": "suspended", "cause": "balance_used_up",
                     "since": "2026-11-15T00:00:00Z", "suspends_at": "2026-11-16T00:00:00Z", "pay_by": "2026-11-15T23:59:59Z",
                     "action": {"kind": "top_up", "portal_path": "/billing/top-up", "amount_usd": "125.50"}}}}
        """;

    /// <summary>
    /// Given a 402 billing_payment_required with its details / When a read fails with it / Then it is a
    /// BillingPaymentRequiredException, a PaymentRequiredException, with its tenant, profile, stage, cause,
    /// instants and action read from the details, the action's amount an exact decimal; a 402 with a code
    /// this version does not know is a PaymentRequiredException by its status.
    /// </summary>
    [Fact]
    public async Task A_payment_required_answer_is_typed_with_its_details()
    {
        using var api = new FakeApi().Answer(Refusal, 402).Fail(402, "payment_needed_somehow");
        using var client = api.Client();

        var billing = await Assert.ThrowsAsync<BillingPaymentRequiredException>(() => client.Usage.GetAsync());
        var other = await Assert.ThrowsAsync<PaymentRequiredException>(() => client.Usage.GetAsync());

        Assert.IsAssignableFrom<PaymentRequiredException>(billing);
        Assert.Equal(("billing_payment_required", 402, "Top up your balance to keep trading"), (billing.Code, billing.Status, billing.Message));
        Assert.Equal(("t-1", "bp-1", "suspended", "balance_used_up"), (billing.TenantId, billing.BillingProfileId, billing.Stage, billing.BillingCause));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), billing.SuspendsAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 23, 59, 59, TimeSpan.Zero), billing.PayBy);
        Assert.Equal(new PaymentAction("top_up", "/billing/top-up", 125.50m, null), billing.Action);
        Assert.Equal("125.50", billing.Action!.AmountUsd!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsNotType<BillingPaymentRequiredException>(other);
        Assert.Equal(("payment_needed_somehow", 402), (other.Code, other.Status));
    }

    /// <summary>
    /// Given a billing refusal with no action, or one whose action lacks its portal path / When its details
    /// are read / Then the action is null and nothing else is invented: absent fields read as null.
    /// </summary>
    [Fact]
    public async Task An_absent_action_or_field_reads_as_null()
    {
        using var api = new FakeApi().Fail(402, "billing_payment_required", "pay", """{"stage": "reminder", "action": {"kind": "top_up"}}""");
        using var client = api.Client();

        var billing = await Assert.ThrowsAsync<BillingPaymentRequiredException>(() => client.Farms.GetAsync(Guid.Parse(FakeApi.Id1)));

        Assert.Equal("reminder", billing.Stage);
        Assert.Null(billing.Action);
        Assert.Null(billing.TenantId);
        Assert.Null(billing.Since);
    }
}

/// <summary>A billing refusal on a write and on an order: writes throw it, execution returns it.</summary>
public sealed class BillingRefusalTests
{
    /// <summary>
    /// Given a suspended billing profile / When a terminal is asked for and an order is placed / Then the
    /// write throws BillingPaymentRequiredException once, never sent again, and the order comes back as a
    /// refused result, not retryable, whose detail carries the error's text: execution never throws.
    /// </summary>
    [Fact]
    public async Task A_write_throws_it_and_an_order_returns_it()
    {
        using var api = new FakeApi().Answer(PaymentRequiredTests.Refusal, 402).Answer(PaymentRequiredTests.Refusal, 402);
        using var client = api.Client();

        await Assert.ThrowsAsync<BillingPaymentRequiredException>(
            () => client.Terminals.CreateAsync(Guid.Parse(FakeApi.Id1), Guid.Parse(FakeApi.Id2), "t-1"));
        var order = await client.Execution.PlaceOrderAsync(Guid.Parse(FakeApi.Id1), "EURUSD", Side.Buy, 0.01m, "o-1");

        Assert.Equal((false, Reason.Refused, false), (order.Ok, order.Reason, order.Retryable));
        Assert.Equal("Top up your balance to keep trading", order.Detail);
        Assert.Equal(2, api.Requests.Count);
    }
}

/// <summary>The billing reads.</summary>
public sealed class BillingReadsTests
{
    /// <summary>
    /// Given a billing profile and an invoice / When the price list, summary, profile, a month's forecast,
    /// the prices and the invoice are read / Then each is one GET on its route, the month as a query
    /// parameter, and nothing is sent for a month left out.
    /// </summary>
    [Fact]
    public async Task Each_billing_read_is_one_get_on_its_route()
    {
        using var api = new FakeApi();
        for (var i = 0; i < 7; i++)
        {
            api.Answer("{}");
        }
        using var client = api.Client();
        var b = client.Billing;
        var profile = Guid.Parse(FakeApi.Id1);

        await b.PriceListAsync();
        await b.SummaryAsync();
        await b.ProfileAsync(profile);
        await b.ForecastAsync(profile, month: "2026-10");
        await b.ForecastAsync(profile);
        await b.PricesAsync();
        await b.InvoiceAsync(Guid.Parse(FakeApi.Id2));

        var p = "/v1/billing/profiles/" + FakeApi.Id1;
        Assert.Equal(
            ["/v1/price-list", "/v1/billing", p, p + "/forecast?month=2026-10", p + "/forecast", "/v1/billing/prices", "/v1/billing/invoices/" + FakeApi.Id2],
            api.Requests.Select(r => r.PathAndQuery));
        Assert.All(api.Requests, r => Assert.Equal("GET", r.Method));
    }
}

/// <summary>The invoices, one page or every one.</summary>
public sealed class InvoicesTests
{
    /// <summary>
    /// Given invoices on two pages / When one profile's overdue and due invoices are walked with a page size /
    /// Then the profile and the states go on every page, the states comma-separated, and the cursor is
    /// followed to its end; a state the enum does not define is refused before any request.
    /// </summary>
    [Fact]
    public async Task Invoices_follow_the_cursor_narrowed_by_profile_and_state()
    {
        var row = $$"""{"id": "{{FakeApi.Id2}}", "state": "overdue"}""";
        using var api = new FakeApi().Answer(FakeApi.PageOf("c-2", row)).Answer(FakeApi.PageOf(null, row)).Answer(FakeApi.PageOf(null));
        using var client = api.Client();
        var profile = Guid.Parse(FakeApi.Id1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Billing.InvoicesPageAsync(state: [(BillingInvoiceState)77]));
        var states = new List<BillingInvoiceState>();
        await foreach (var invoice in client.Billing.InvoicesAsync(profile, [BillingInvoiceState.Overdue, BillingInvoiceState.Due], limit: 1))
        {
            states.Add(invoice.State);
        }
        var page = await client.Billing.InvoicesPageAsync(cursor: "c-9", limit: 200);

        Assert.Equal([BillingInvoiceState.Overdue, BillingInvoiceState.Overdue], states);
        Assert.Null(page.NextCursor);
        var p = "/v1/billing/invoices";
        Assert.Equal(
            [$"{p}?billing_profile_id={FakeApi.Id1}&state=overdue%2Cdue&limit=1", $"{p}?billing_profile_id={FakeApi.Id1}&state=overdue%2Cdue&cursor=c-2&limit=1",
             $"{p}?cursor=c-9&limit=200"],
            api.Requests.Select(r => r.PathAndQuery));
    }
}

/// <summary>Enriched complete: nothing held as pending, and no error shadows a member of System.Exception.</summary>
public sealed class EnrichedCompleteTests
{
    /// <summary>
    /// Given every exception type of the SDK / When their public properties are read / Then none has the name
    /// of a member of System.Exception (InnerException, Data, Source and the rest), so billing's cause is
    /// BillingCause; and the idiom map holds nothing as pending: every entry of the reference surface maps.
    /// </summary>
    [Fact]
    public void No_error_shadows_an_exception_member_and_nothing_is_pending()
    {
        var reserved = typeof(Exception).GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var shadowing = typeof(MfxClient).Assembly.GetExportedTypes()
            .Where(t => typeof(Exception).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => reserved.Contains(name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..]))
            .ToList();

        Assert.Empty(shadowing);
        Assert.Null(typeof(BillingPaymentRequiredException).GetProperty("Cause"));
        Assert.Empty(Surface.Map["pending"]!.AsObject());
        Assert.Empty(Surface.Missing("core"));
        Assert.Empty(Surface.Missing("enriched"));
    }
}
