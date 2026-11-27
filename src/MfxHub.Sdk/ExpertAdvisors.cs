using System.Text;
using System.Text.Json;
using MfxHub.Models;

namespace MfxHub;

/// <summary>One record the expert wrote; <c>(Segment, Line)</c> is its place in the expert's files, the same for a record seen twice.</summary>
/// <param name="Segment">The file segment.</param>
/// <param name="Line">The line in it.</param>
/// <param name="Record">The record, as the expert wrote it.</param>
public sealed record EaRecord(long Segment, long Line, IReadOnlyDictionary<string, JsonElement> Record);

/// <summary>
/// A batch of records an expert wrote (<c>expert_advisor.telemetry</c> on an <c>ea:</c> channel), in the
/// order written. <see cref="Skipped"/> and <see cref="Dropped"/> count since the message before.
/// </summary>
/// <param name="Channel">The channel.</param>
/// <param name="Seq">Its seq.</param>
/// <param name="Records">The records.</param>
/// <param name="Skipped">Lines the platform could not read.</param>
/// <param name="Dropped">Records left out by the platform.</param>
[ContractSchema("EaTelemetryMessage")]
public sealed record EaTelemetry(string Channel, long? Seq, IReadOnlyList<EaRecord> Records, long Skipped, long Dropped);

/// <summary>
/// The expert wrote faster than its records could be delivered, and the oldest waiting files were
/// discarded (<c>expert_advisor.telemetry_lost</c>). Trading is never held for telemetry.
/// </summary>
/// <param name="Channel">The channel.</param>
/// <param name="Seq">Its seq.</param>
/// <param name="SegmentFrom">The first segment discarded.</param>
/// <param name="SegmentTo">The last segment discarded.</param>
/// <param name="Records">How many records they held.</param>
[ContractSchema("EaTelemetryLostMessage")]
public sealed record EaTelemetryLost(string Channel, long? Seq, long SegmentFrom, long SegmentTo, long Records);

/// <summary>The tenant's Expert Advisor versions (<c>client.ExpertAdvisors</c>; <c>ea:read</c> to read, <c>ea:write</c> to upload).</summary>
public sealed class ExpertAdvisors
{
    /// <summary>The message that carries an expert's records.</summary>
    public const string Telemetry = "expert_advisor.telemetry";

    /// <summary>The message that says records were discarded.</summary>
    public const string TelemetryLost = "expert_advisor.telemetry_lost";

    /// <summary>A version stops changing on its own once it is in one of these (<c>judged</c> is on the way to one).</summary>
    public static readonly IReadOnlySet<string> Settled = new HashSet<string>(["approved", "held", "rejected", "withdrawn", "revoked"], StringComparer.Ordinal);

    private static readonly TimeSpan PollFrom = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollCap = TimeSpan.FromSeconds(15);

    private readonly MfxClient _client;

    internal ExpertAdvisors(MfxClient client) => _client = client;

    private static string Path(Guid id, string rest = "") => "/expert-advisors/" + Wire.Id(id) + rest;

    /// <summary>
    /// A message from an <c>ea:</c> channel as what it is (an <see cref="EaTelemetry"/> or an
    /// <see cref="EaTelemetryLost"/>), or null for any other message.
    /// </summary>
    /// <param name="message">A message from the stream.</param>
    /// <returns>The telemetry, or null.</returns>
    public static object? TelemetryOf(StreamMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type == Telemetry)
        {
            var d = new EventData(Telemetry, message.Data);
            var records = d.Parts("records").Select(r => new EaRecord(r.Integer("segment"), r.Integer("line"),
                r.Raw("record", true).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal))).ToList();
            return new EaTelemetry(message.Channel, message.Seq, records, d.IntegerOrNull("skipped") ?? 0, d.IntegerOrNull("dropped") ?? 0);
        }
        if (message.Type == TelemetryLost)
        {
            var d = new EventData(TelemetryLost, message.Data);
            return new EaTelemetryLost(message.Channel, message.Seq, d.Integer("segment_from"), d.Integer("segment_to"), d.Integer("records"));
        }
        return null;
    }

    /// <summary>
    /// Uploads a bundle: path to source, exactly one <c>.mq5</c> and any <c>.mqh</c> it includes, as text.
    /// Never a compiled file. A new bundle answers its version <c>uploaded</c> with the checks started; the
    /// same bundle again answers the version it made, as it stands.
    /// </summary>
    /// <param name="name">The expert's name.</param>
    /// <param name="files">Each file's path and its source, as text (sent as UTF-8).</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version.</returns>
    public Task<TenantExpertAdvisor> UploadAsync(
        string name, IReadOnlyDictionary<string, string> files, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(files);
        var body = new ExpertAdvisorUpload
        {
            Name = name,
            Files = [.. files.Select(f => new Files { Path = f.Key, ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Value)) })],
        };
        return _client.WriteAsync<TenantExpertAdvisor>(HttpMethod.Post, "/expert-advisors", body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>One version: its state, verdict, each check and the terminals that carry it. Never its bytes.</summary>
    /// <param name="expertAdvisorId">The version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version.</returns>
    public Task<TenantExpertAdvisor> GetAsync(Guid expertAdvisorId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<TenantExpertAdvisor>(Path(expertAdvisorId), null, cancellationToken);

    /// <summary>One page of the tenant's versions, newest first.</summary>
    /// <param name="cursor">The cursor of the page before.</param>
    /// <param name="limit">How many the page may hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<TenantExpertAdvisor>> PageAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.PageAsync<TenantExpertAdvisor>("/expert-advisors", new Query().Add("cursor", cursor).Add("limit", limit), cancellationToken);

    /// <summary>Every version, following the cursor to the end.</summary>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every version.</returns>
    public IAsyncEnumerable<TenantExpertAdvisor> ListAsync(int? limit = null, CancellationToken cancellationToken = default) =>
        Pages.All((cursor, token) => PageAsync(cursor, limit, token), cancellationToken);

    /// <summary>The checks' report: each finding with its rule, colour and line. 409 until the verdict is set.</summary>
    /// <param name="expertAdvisorId">The version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report.</returns>
    public Task<EaReport> ReportAsync(Guid expertAdvisorId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<EaReport>(Path(expertAdvisorId, "/report"), null, cancellationToken);

    /// <summary>Withdraws a version; a check still running stops. 409 while a terminal carries it.</summary>
    /// <param name="expertAdvisorId">The version.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version.</returns>
    public Task<TenantExpertAdvisor> DeleteAsync(Guid expertAdvisorId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _client.WriteAsync<TenantExpertAdvisor>(HttpMethod.Delete, Path(expertAdvisorId), null, Keys.Required(idempotencyKey), cancellationToken);

    /// <summary>
    /// Reads the version until its checks have settled it (<see cref="Settled"/>), or until
    /// <paramref name="timeout"/> has passed on the client's clock, 2 s between reads doubling to 15 s.
    /// Returns the last read either way.
    /// </summary>
    /// <param name="expertAdvisorId">The version.</param>
    /// <param name="timeout">How long to wait; 600 s when null.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The version as last read.</returns>
    public async Task<TenantExpertAdvisor> WaitForVerdictAsync(Guid expertAdvisorId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var clock = _client.Clock;
        var started = clock.GetTimestamp();
        var limit = timeout ?? TimeSpan.FromSeconds(600);
        var wait = PollFrom;
        var version = await GetAsync(expertAdvisorId, cancellationToken).ConfigureAwait(false);
        while (!Settled.Contains(Wire.Word(version.State)) && clock.GetElapsedTime(started) < limit)
        {
            await _client.PauseAsync(wait < PollCap ? wait : PollCap, cancellationToken).ConfigureAwait(false);
            wait *= 2;
            version = await GetAsync(expertAdvisorId, cancellationToken).ConfigureAwait(false);
        }
        return version;
    }
}

/// <summary>What the tenant reads about itself (<c>client.Tenant</c>).</summary>
public sealed class Tenant
{
    private readonly MfxClient _client;

    internal Tenant(MfxClient client) => _client = client;

    /// <summary>The tenant's contacts and their roles, as an operator set them. Read-only for a tenant.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The contacts.</returns>
    public Task<OwnContacts> ContactsAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<OwnContacts>("/tenant/contacts", null, cancellationToken);

    /// <summary>
    /// What the organisation must still accept before it can add anything:
    /// <c>Due</c> while a plan change awaits acceptance of the new plan's Service Agreement, or a version of a
    /// Service Agreement, the Terms of Service or the MetaTrader 5 licence asks to be accepted; <c>Held</c> while
    /// additions are refused (<see cref="ServiceAgreementNotAcceptedException"/>, <see cref="TermsNotAcceptedException"/>).
    /// An owner or admin accepts at <c>AcceptUrl</c>. Any key may read it, whatever its scopes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What is due, the plan change if any, each document and where to accept.</returns>
    public Task<TenantServiceAgreement> ServiceAgreementAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync<TenantServiceAgreement>("/tenant/service-agreement", null, cancellationToken);
}
