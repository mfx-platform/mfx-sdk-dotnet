using System.Text.Json;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// Farms, usage and maintenance, with the maintenance window event.
/// </summary>
public sealed class FarmsUsageMaintenanceTests
{
    private static string Window(string id, string state = "scheduled") =>
        $$"""{"id": "{{id}}", "label": "cell patch", "state": "{{state}}", "starts_at": "2026-11-20T02:00:00Z", "ends_at": "2026-11-20T03:00:00Z", "expected_minutes": 60, "suspended": [], "terminals": 2}""";

    /// <summary>
    /// Given two pages of farms and a farm's id / When every farm is listed and one is read / Then the
    /// cursor is followed with the page size asked each time, and the farm is GET /farms/{id}; usage is
    /// one GET of /usage.
    /// </summary>
    [Fact]
    public async Task Farms_are_listed_by_cursor_and_usage_is_one_read()
    {
        var farm = $$"""{"id": "{{FakeApi.Id1}}", "name": "shared-eu", "os": "windows", "region": "westeurope", "usages": [], "redundancy": "none", "instruments_max": 50, "status": "active"}""";
        using var api = new FakeApi().Answer(FakeApi.PageOf("c-2", farm)).Answer(FakeApi.PageOf(null)).Answer(farm)
            .Answer("""{"plan": "standard", "key": {}, "tenant": {}, "farms": [], "per_login": {}, "lifecycle": {}, "webhooks": {}, "as_of": "2026-11-17T09:00:00Z"}""");
        using var client = api.Client();

        var names = new List<string>();
        await foreach (var listed in client.Farms.ListAsync(limit: 1))
        {
            names.Add(listed.Name);
        }
        var one = await client.Farms.GetAsync(Guid.Parse(FakeApi.Id1));
        var usage = await client.Usage.GetAsync();

        Assert.Equal(["shared-eu"], names);
        Assert.Equal(TenantFarmStatus.Active, one.Status);
        Assert.Equal(UsagePlan.Standard, usage.Plan);
        Assert.Equal(["/v1/farms?limit=1", "/v1/farms?cursor=c-2&limit=1", "/v1/farms/" + FakeApi.Id1, "/v1/usage"], api.Requests.Select(r => r.PathAndQuery));
    }

    /// <summary>
    /// Given maintenance windows in two states / When they are listed by state / Then the states go as one
    /// comma-separated value of the contract's words, a state the enum does not define is refused before
    /// any request, and a window's terminals follow their own cursor.
    /// </summary>
    [Fact]
    public async Task Windows_are_narrowed_by_state_and_their_terminals_follow_the_cursor()
    {
        var terminal = $$"""{"terminal_id": "{{FakeApi.Id2}}", "state": "waiting"}""";
        using var api = new FakeApi().Answer(FakeApi.PageOf(null, Window(FakeApi.Id1, "in_progress")))
            .Answer(FakeApi.PageOf("c-2", terminal)).Answer(FakeApi.PageOf(null, terminal));
        using var client = api.Client();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Maintenance.PageAsync(state: [(TenantMaintenanceState)42]));
        var windows = new List<TenantMaintenance>();
        await foreach (var w in client.Maintenance.ListAsync(state: [TenantMaintenanceState.Scheduled, TenantMaintenanceState.InProgress]))
        {
            windows.Add(w);
        }
        var count = 0;
        await foreach (var _ in client.Maintenance.TerminalsAsync(Guid.Parse(FakeApi.Id1), limit: 1))
        {
            count++;
        }

        Assert.Equal(TenantMaintenanceState.InProgress, Assert.Single(windows).State);
        Assert.Equal(2, count);
        Assert.Equal(
            [
                "/v1/maintenance?state=scheduled%2Cin_progress",
                $"/v1/maintenance/{FakeApi.Id1}/terminals?limit=1",
                $"/v1/maintenance/{FakeApi.Id1}/terminals?cursor=c-2&limit=1",
            ],
            api.Requests.Select(r => r.PathAndQuery));
    }

    /// <summary>
    /// Given a message on events that says a window started, and a message of another type / When each
    /// is read as a window change / Then the first is the window as it stands after the change, its
    /// terminals still the digits the stream wrote, and the other is null.
    /// </summary>
    [Fact]
    public void A_window_change_on_the_stream_reads_as_one()
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            $$"""{"schema_version": "1", "window_id": "{{FakeApi.Id1}}", "label": "cell patch", "state": "in_progress", "starts_at": "2026-11-20T02:00:00Z", "ends_at": "2026-11-20T03:00:00Z", "message": "", "terminals": "2", "change": "started"}""")!;

        var change = Maintenance.WindowEvent(new StreamMessage(Channel.Events, 7, Maintenance.WindowChanged, data));
        var other = Maintenance.WindowEvent(new StreamMessage(Channel.Events, 8, "terminal.state_changed", data));

        Assert.NotNull(change);
        Assert.Equal(MaintenanceWindowChangedChange.Started, change.Change);
        Assert.Equal("2", change.Terminals);
        Assert.Equal("maintenance.window_changed", Maintenance.WindowChanged);
        Assert.Null(other);
    }
}
