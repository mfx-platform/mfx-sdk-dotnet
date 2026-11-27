using System.Text.Json;
using MfxHub.Models;

namespace MfxHub;

/// <summary>The maintenance windows that reach this tenant, and its terminals in each (<c>client.Maintenance</c>).</summary>
public sealed class Maintenance
{
    /// <summary>The message on <c>events</c>, and the webhook event type, that says a window reached the tenant or moved.</summary>
    public const string WindowChanged = "maintenance.window_changed";

    private readonly MfxClient _client;

    internal Maintenance(MfxClient client) => _client = client;

    /// <summary>
    /// A message from <c>events</c> as the window change it is, or null for any other. It is sent when
    /// a window is announced, starts, has its expected end or its message moved, ends or is cancelled;
    /// <c>change</c> says which. On the stream every value arrives as text, so <c>terminals</c> is a
    /// count written as digits.
    /// </summary>
    /// <param name="message">A message from the stream.</param>
    /// <returns>The window change, or null.</returns>
    public static MaintenanceWindowChanged? WindowEvent(StreamMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type != WindowChanged)
        {
            return null;
        }
        return JsonSerializer.SerializeToElement(message.Data, MfxJson.Options).Deserialize<MaintenanceWindowChanged>(MfxJson.Options);
    }

    /// <summary>
    /// One page of windows. Unfiltered: scheduled and in progress, soonest first. <c>completed</c>
    /// lists the last 30 days; a scheduled window is listed once it has been announced.
    /// </summary>
    /// <param name="state">The states to list, sent as one comma-separated value; each is checked here.</param>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<TenantMaintenance>> PageAsync(
        IEnumerable<TenantMaintenanceState>? state = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var wanted = WireNames.Joined(state, nameof(state)); // before the call: a bad one is the caller's
        return _client.PageAsync<TenantMaintenance>(
            "/maintenance", new Query().Add("cursor", cursor).Add("limit", limit).Add("state", wanted), cancellationToken);
    }

    /// <summary>Every window, following the cursor.</summary>
    /// <param name="state">As <see cref="PageAsync"/>.</param>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every window, one request a page.</returns>
    public IAsyncEnumerable<TenantMaintenance> ListAsync(
        IEnumerable<TenantMaintenanceState>? state = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var states = state?.ToList();
        return Pages.All((cursor, token) => PageAsync(states, cursor, limit, token), cancellationToken);
    }

    /// <summary>
    /// One page of the tenant's terminals in one window, each with its state and expected time back. A
    /// window the tenant is not in, or another tenant's, is 404.
    /// </summary>
    /// <param name="windowId">The window.</param>
    /// <param name="cursor">The cursor of the page before; the first page when null.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<TenantMaintenanceTerminal>> TerminalsPageAsync(
        Guid windowId, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.PageAsync<TenantMaintenanceTerminal>(
            "/maintenance/" + Wire.Id(windowId) + "/terminals", new Query().Add("cursor", cursor).Add("limit", limit), cancellationToken);

    /// <summary>Every one of the tenant's terminals in the window, following the cursor.</summary>
    /// <param name="windowId">The window.</param>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every terminal in the window, one request a page.</returns>
    public IAsyncEnumerable<TenantMaintenanceTerminal> TerminalsAsync(
        Guid windowId, int? limit = null, CancellationToken cancellationToken = default) =>
        Pages.All((cursor, token) => TerminalsPageAsync(windowId, cursor, limit, token), cancellationToken);
}
