using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The catalog (brokers, servers, catalogues, symbols and quotes): the broker's own names, the server and account type
/// that say which, and prices kept exactly.
/// </summary>
public sealed class CatalogTests
{
    private static readonly Guid Broker = Guid.Parse(FakeApi.Id1);
    private static readonly Guid Server = Guid.Parse(FakeApi.Id2);

    private static string BrokerRow(string id) => $$"""{"id": "{{id}}", "code": "tb", "display_name": "Test Broker", "status": "active"}""";

    /// <summary>
    /// Given two pages of brokers / When every broker is listed / Then the cursor is followed to its end,
    /// one request a page; and a broker's servers are one page of /brokers/{id}/servers.
    /// </summary>
    [Fact]
    public async Task Every_broker_is_listed_by_following_the_cursor()
    {
        using var api = new FakeApi().Answer(FakeApi.PageOf("c-2", BrokerRow(FakeApi.Id1))).Answer(FakeApi.PageOf(null, BrokerRow(FakeApi.Id2)))
            .Answer(FakeApi.PageOf(null, $$"""{"id": "{{FakeApi.Id3}}", "server_name": "TB-Demo", "kind": "demo", "enabled": false, "disabled_reason": "closed for the weekend"}"""));
        using var client = api.Client();

        var ids = new List<Guid>();
        await foreach (var broker in client.Catalog.AllBrokersAsync())
        {
            ids.Add(broker.Id);
        }
        var servers = await client.Catalog.ServersAsync(Broker, limit: 5);

        Assert.Equal([Guid.Parse(FakeApi.Id1), Guid.Parse(FakeApi.Id2)], ids);
        Assert.Equal(["/v1/brokers", "/v1/brokers?cursor=c-2", $"/v1/brokers/{FakeApi.Id1}/servers?limit=5"], api.Requests.Select(r => r.PathAndQuery));
        Assert.Equal("closed for the weekend", Assert.Single(servers).DisabledReason);
    }

    /// <summary>
    /// Given a symbol in the broker's own name, with a dot and a slash in it / When its spec and its
    /// quote are read with a server and an account type / Then the name is one escaped path segment,
    /// both narrowing parameters are sent, and what is not named is left out of the request.
    /// </summary>
    [Fact]
    public async Task A_symbol_is_named_as_the_broker_names_it_and_narrowed_by_server_and_account_type()
    {
        using var api = new FakeApi().Answer("""{"symbol": "BTC/USD.lv"}""").Answer(Quote()).Answer(FakeApi.PageOf(null));
        using var client = api.Client();

        await client.Catalog.SymbolAsync(Broker, "BTC/USD.lv", serverId: Server, accountType: "pro");
        await client.Catalog.QuoteAsync(Broker, "EURUSD");
        await client.Catalog.SymbolsAsync(Broker, accountType: "pro", limit: 10);

        Assert.Equal(
            [
                $"/v1/brokers/{FakeApi.Id1}/symbols/BTC%2FUSD.lv?server_id={FakeApi.Id2}&account_type=pro",
                $"/v1/brokers/{FakeApi.Id1}/symbols/EURUSD/quote",
                $"/v1/brokers/{FakeApi.Id1}/symbols?account_type=pro&limit=10",
            ],
            api.Requests.Select(r => r.PathAndQuery));
    }

    /// <summary>
    /// Given a quote whose prices carry trailing zeros / When it is read / Then bid and ask keep every
    /// digit as the API wrote them, its age and its market state come back, and a price written as a
    /// JSON number is never read as one.
    /// </summary>
    [Fact]
    public async Task A_quote_keeps_every_digit_and_says_how_old_it_is()
    {
        using var api = new FakeApi().Answer(Quote("1.12120", "1.12180"));
        using var client = api.Client();

        var quote = await client.Catalog.QuoteAsync(Broker, "EURUSD", serverId: Server);

        Assert.Equal("1.12120", quote.Bid);
        Assert.Equal("1.12180", quote.Ask);
        Assert.Equal(0.4, quote.AgeS);
        Assert.Equal(SymbolState.Open, quote.State);
        Assert.Equal(1.12120m, ExactDecimalConverter.Parse(quote.Bid));
    }

    /// <summary>
    /// Given a broker's catalogues and a page of symbols that says it is last known / When they are
    /// read / Then the catalogues are a list with each one's hash, and the page's source is last_known.
    /// </summary>
    [Fact]
    public async Task Catalogues_are_a_list_and_a_symbol_page_says_where_its_rows_came_from()
    {
        using var api = new FakeApi()
            .Answer($$"""{"items": [{"server_id": "{{FakeApi.Id2}}", "account_type": "pro", "source": "live", "catalogue_hash": "h1", "symbols": 120, "refreshed_at": "2026-11-17T09:00:00Z"}]}""")
            .Answer("""{"items": [{"symbol": "EURUSD"}], "next_cursor": "", "source": "last_known"}""");
        using var client = api.Client();

        var catalogues = await client.Catalog.CataloguesAsync(Broker);
        var page = await client.Catalog.SymbolsAsync(Broker, serverId: Server);

        Assert.Equal("h1", Assert.Single(catalogues).CatalogueHash);
        Assert.Equal($"/v1/brokers/{FakeApi.Id1}/catalogues", api.Requests[0].PathAndQuery);
        Assert.Equal("last_known", page.Source);
        Assert.Null(page.NextCursor);
        Assert.Equal("EURUSD", Assert.Single(page).Symbol1);
    }

    private static string Quote(string bid = "1.12129", string ask = "1.12181") =>
        $$"""{"symbol": "EURUSD", "server_id": "{{FakeApi.Id2}}", "account_type": "pro", "bid": "{{bid}}", "ask": "{{ask}}", "tick_time": "2026-11-17T09:00:00Z", "broker_time": "2026-11-17 11:00:00", "age_s": 0.4, "state": "open"}""";
}
