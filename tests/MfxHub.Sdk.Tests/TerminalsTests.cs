using System.Net;
using System.Text;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// Core's one domain call: a terminal by id, one page, and every terminal by cursor.
/// </summary>
public sealed class TerminalsTests
{
    private const string One = "0191f0c2-aaaa-7bbb-8ccc-000000000001";
    private const string Two = "0191f0c2-aaaa-7bbb-8ccc-000000000002";

    private sealed class Serving(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);

        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Terminal(string id, string state = "ACTIVE") =>
        $$"""
        {"id": "{{id}}", "farm": {"id": "{{id}}"}, "broker": {"id": "{{id}}"}, "server": {"id": "{{id}}"},
         "streaming": false, "execution": true, "state": "{{state}}", "instruments": [], "created_at": "2026-10-23T09:00:00Z"}
        """;

    private static string PageOf(string? cursor, params string[] terminals) =>
        "{\"items\": [" + string.Join(",", terminals) + "], \"next_cursor\": "
        + (cursor is null ? "null" : "\"" + cursor + "\"") + ", \"returned\": " + terminals.Length + "}";

    private static MfxClient Client(Serving serving) =>
        new(new Uri("https://api.example.test/v1"), "mfxk_1", SigningKey.FromSeed(SigningTests.TestSeed),
            new MfxClientOptions { Handler = serving, Pause = (_, _) => Task.CompletedTask });

    /// <summary>
    /// Given a terminal's id / When it is read / Then one GET of /terminals/{id} is made and the
    /// terminal comes back as the generated model, its state the enum's.
    /// </summary>
    [Fact]
    public async Task A_terminal_is_read_by_its_id()
    {
        using var serving = new Serving(Terminal(One, "QUEUED"));
        using var client = Client(serving);

        var terminal = await client.Terminals.GetAsync(Guid.Parse(One));

        Assert.Equal("/v1/terminals/" + One, Assert.Single(serving.Asked).PathAndQuery);
        Assert.Equal(Guid.Parse(One), terminal.Id);
        Assert.Equal(TenantTerminalState.Queued, terminal.State);
    }

    /// <summary>
    /// Given a cursor, a limit, labels and states / When one page is read / Then one request carries
    /// each as the contract reads it (each label as key:value, the states as one comma-separated value
    /// of the contract's words), and the page has its items, its cursor and its count, with no total;
    /// an empty cursor on the last page reads as none.
    /// </summary>
    [Fact]
    public async Task One_page_is_one_request_with_its_filters_as_the_contract_reads_them()
    {
        using var serving = new Serving(PageOf("c-3", Terminal(One), Terminal(Two)), PageOf(""));
        using var client = Client(serving);

        var page = await client.Terminals.PageAsync(
            cursor: "c-2", limit: 2,
            label: new Dictionary<string, string> { ["desk"] = "fx" },
            state: [TenantTerminalState.Active, TenantTerminalState.LoggingIn]);
        var last = await client.Terminals.PageAsync();

        Assert.Equal("/v1/terminals?cursor=c-2&limit=2&label=desk%3Afx&state=ACTIVE%2CLOGGING_IN", serving.Asked[0].PathAndQuery);
        Assert.Equal(2, page.Count);
        Assert.Equal(2, page.Returned);
        Assert.Equal("c-3", page.NextCursor);
        Assert.Equal([Guid.Parse(One), Guid.Parse(Two)], page.Select(t => t.Id));
        Assert.Equal("/v1/terminals", serving.Asked[1].PathAndQuery);
        Assert.Null(last.NextCursor);
        Assert.Empty(last);
    }

    /// <summary>Given a state the enum does not define / When a page is asked for / Then it is refused before any request.</summary>
    [Fact]
    public async Task A_state_that_is_not_one_is_refused_before_any_request()
    {
        using var serving = new Serving();
        using var client = Client(serving);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.Terminals.PageAsync(state: [(TenantTerminalState)99]));

        Assert.Empty(serving.Asked);
    }

    /// <summary>
    /// Given three pages joined by cursors / When every terminal is listed / Then the list follows
    /// next_cursor until it is null, one request a page, asking each page for the same size and
    /// filters, and yields the terminals in order.
    /// </summary>
    [Fact]
    public async Task Every_terminal_is_listed_by_following_the_cursor()
    {
        using var serving = new Serving(PageOf("c-2", Terminal(One)), PageOf("c-3"), PageOf(null, Terminal(Two)));
        using var client = Client(serving);

        var ids = new List<Guid>();
        await foreach (var terminal in client.Terminals.ListAsync(limit: 1, state: [TenantTerminalState.Active]))
        {
            ids.Add(terminal.Id);
        }

        Assert.Equal([Guid.Parse(One), Guid.Parse(Two)], ids);
        Assert.Equal(
            ["/v1/terminals?limit=1&state=ACTIVE", "/v1/terminals?cursor=c-2&limit=1&state=ACTIVE", "/v1/terminals?cursor=c-3&limit=1&state=ACTIVE"],
            serving.Asked.Select(u => u.PathAndQuery));
    }
}
