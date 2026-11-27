using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// Waiting on a terminal and on its operations, reading an operation, the journal, and whether a
/// terminal is usable.
/// </summary>
public sealed class TerminalWaitsTests
{
    private static readonly Guid One = Guid.Parse(FakeApi.Id1);
    private static readonly Guid Op = Guid.Parse(FakeApi.Id2);

    private static string Operation(string state) =>
        $$"""{"operation_id": "{{FakeApi.Id2}}", "kind": "restart", "terminal_id": "{{FakeApi.Id1}}", "state": "{{state}}", "requested_by": "key", "reason": "r", "timeline": [], "created_at": "2026-11-17T09:00:00Z"}""";

    /// <summary>
    /// Given a terminal QUEUED with an expected start 40 s away, then PROVISIONING, then ACTIVE / When
    /// it is waited on / Then it is read again at the expected start, then after the backoff, and the
    /// ACTIVE terminal is returned; only ACTIVE is usable.
    /// </summary>
    [Fact]
    public async Task A_queued_terminal_is_read_again_at_its_expected_start()
    {
        using var api = new FakeApi();
        api.Answer(FakeApi.Terminal(state: "QUEUED", extra: """, "queue": {"queued_at": "2026-11-17T08:59:00Z", "expected_start_at": "2026-11-17T09:00:40Z"}"""))
            .Answer(FakeApi.Terminal(state: "PROVISIONING"))
            .Answer(FakeApi.Terminal(state: "ACTIVE"));
        using var client = api.Client();

        var terminal = await client.Terminals.WaitUntilActiveAsync(One);

        Assert.Equal(TenantTerminalState.Active, terminal.State);
        Assert.True(Terminals.IsUsable(terminal));
        Assert.Equal([TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(4)], api.Pauses);
        Assert.Equal(3, api.Requests.Count);
    }

    /// <summary>
    /// Given a terminal that stays PROVISIONING / When it is waited on for 10 s / Then the waits never
    /// add up to more than 10 s and the last read is returned as it is, still on its way; a QUEUED
    /// terminal due in a second is read again no sooner than 2 s, and one due in an hour within a minute.
    /// </summary>
    [Fact]
    public async Task A_wait_that_runs_out_returns_the_last_read()
    {
        using var api = new FakeApi();
        for (var i = 0; i < 4; i++)
        {
            api.Answer(FakeApi.Terminal(state: "PROVISIONING"));
        }
        using var client = api.Client();

        var terminal = await client.Terminals.WaitUntilActiveAsync(One, TimeSpan.FromSeconds(10));

        Assert.Equal(TenantTerminalState.Provisioning, terminal.State);
        Assert.False(Terminals.IsUsable(terminal));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)], api.Pauses);
        var now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        TenantTerminal Queued(int inSeconds) => new() { State = TenantTerminalState.Queued, Queue = new TerminalQueue { ExpectedStartAt = now.AddSeconds(inSeconds) } };
        Assert.Equal(TimeSpan.FromSeconds(2), Terminals.NextWait(Queued(1), TimeSpan.FromSeconds(8), now));
        Assert.Equal(TimeSpan.FromSeconds(60), Terminals.NextWait(Queued(3600), TimeSpan.FromSeconds(8), now));
        Assert.Equal(TimeSpan.FromSeconds(15), Terminals.NextWait(new TenantTerminal { State = TenantTerminalState.LoggingIn }, TimeSpan.FromSeconds(32), now));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Terminals.WaitUntilActiveAsync(One, TimeSpan.FromSeconds(-1)));
    }

    /// <summary>
    /// Given an operation that is ACCEPTED, then RELAUNCHING, then SUCCEEDED / When it is waited on /
    /// Then it is read on GET /terminals/{id}/operations/{operation_id} after 2 s and then 4 s, and the
    /// final read is returned; one that never ends is returned as it is when the time runs out.
    /// </summary>
    [Fact]
    public async Task An_operation_is_read_until_it_is_over()
    {
        using var api = new FakeApi().Answer(Operation("ACCEPTED")).Answer(Operation("RELAUNCHING")).Answer(Operation("SUCCEEDED"))
            .Answer(Operation("CLOSING")).Answer(Operation("CLOSING")).Answer(Operation("CLOSING"));
        using var client = api.Client();

        var done = await client.Terminals.WaitForOperationAsync(One, Op);
        var running = await client.Terminals.WaitForOperationAsync(One, Op, TimeSpan.FromSeconds(5));

        Assert.Equal(TerminalOperationState.Succeeded, done.State);
        Assert.Equal(TerminalOperationState.Closing, running.State);
        Assert.All(api.Requests, r => Assert.Equal($"/v1/terminals/{FakeApi.Id1}/operations/{FakeApi.Id2}", r.PathAndQuery));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], api.Pauses);
        Assert.Contains("SUPERSEDED", Terminals.OperationFinal);
        Assert.Contains("LOGGING_IN", Terminals.OnItsWay);
    }

    /// <summary>
    /// Given a terminal's journal / When it is read with 50 lines, or with 0 or 201 / Then the lines go
    /// as a query parameter, and a count outside 1 to 200 is refused before any request.
    /// </summary>
    [Fact]
    public async Task The_journal_is_read_with_a_line_count_from_one_to_two_hundred()
    {
        using var api = new FakeApi().Answer(
            $$"""{"terminal_id": "{{FakeApi.Id1}}", "from": "live", "read_at": "2026-11-17T09:00:00Z", "lines": [{"at": "2026-11-17T08:59:59Z", "text": "connected"}]}""");
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Terminals.JournalAsync(One, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Terminals.JournalAsync(One, 201));
        var journal = await client.Terminals.JournalAsync(One, 50);

        Assert.Equal(TerminalJournalFrom.Live, journal.From);
        Assert.Equal($"/v1/terminals/{FakeApi.Id1}/journal?lines=50", api.Only.PathAndQuery);
    }
}
