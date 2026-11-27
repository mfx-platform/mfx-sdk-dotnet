using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MfxHub.Models;

namespace MfxHub;

/// <summary>
/// A webhook delivery that is not provably from MFX Hub: refuse it. <see cref="Reason"/> is one word:
/// <c>malformed</c> (no <c>t</c> or no <c>v1</c>), <c>stale</c> (outside the tolerance) or <c>mismatch</c>.
/// </summary>
public sealed class WebhookSignatureException : MfxException
{
    /// <inheritdoc/>
    public WebhookSignatureException() => Reason = "mismatch";

    /// <inheritdoc/>
    public WebhookSignatureException(string message) : base(message) => Reason = "mismatch";

    /// <inheritdoc/>
    public WebhookSignatureException(string message, Exception inner) : base(message, inner) => Reason = "mismatch";

    /// <summary>A refusal of a delivery, for <paramref name="reason"/>.</summary>
    /// <param name="reason"><c>malformed</c>, <c>stale</c> or <c>mismatch</c>.</param>
    /// <param name="message">What was wrong.</param>
    public WebhookSignatureException(string reason, string message) : base(message) => Reason = reason;

    /// <summary><c>malformed</c>, <c>stale</c> or <c>mismatch</c>.</summary>
    public string Reason { get; }
}

/// <summary>The tenant's webhook endpoints (<c>client.Webhooks</c>; <c>hook:read</c> to read, <c>hook:write</c> to change).</summary>
public sealed class Webhooks
{
    /// <summary>The header every delivery is signed in.</summary>
    public const string SignatureHeader = "MFX-Signature";

    /// <summary>The header naming the event, the stream cursor a replay starts after.</summary>
    public const string EventIdHeader = "MFX-Event-Id";

    /// <summary>The header naming the event's type.</summary>
    public const string EventTypeHeader = "MFX-Event-Type";

    /// <summary>The header counting the delivery's attempts.</summary>
    public const string AttemptHeader = "MFX-Delivery-Attempt";

    /// <summary>How far the signed time may be from the receiver's clock, either way.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(300);

    private readonly MfxClient _client;

    internal Webhooks(MfxClient client) => _client = client;

    private static string Path(Guid endpointId, string rest = "") => "/webhooks/" + Wire.Id(endpointId) + rest;

    /// <summary>
    /// Checks one delivery's <c>MFX-Signature</c> and returns its signed time (unix seconds). The delivery
    /// is genuine when any one of <paramref name="secrets"/> (the new one and the previous, for the day
    /// after a rotation) made any one of the header's <c>v1</c> values, compared in constant time.
    /// </summary>
    /// <param name="body">The request body exactly as received.</param>
    /// <param name="header">The <c>MFX-Signature</c> header.</param>
    /// <param name="secrets">The endpoint's secret as it was shown, or several.</param>
    /// <param name="tolerance">How far the signed time may be from <paramref name="now"/>; 300 s when null.</param>
    /// <param name="now">The receiver's clock; the system's when null.</param>
    /// <returns>The signed time, in unix seconds.</returns>
    /// <exception cref="WebhookSignatureException">The delivery is malformed, stale, or signed by none of the secrets.</exception>
    public static long Verify(
        ReadOnlySpan<byte> body, string header, IEnumerable<Secret> secrets, TimeSpan? tolerance = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(secrets);
        var (stamp, signatures) = Parts(header);
        var clock = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() / 1000.0;
        var limit = (tolerance ?? Tolerance).TotalSeconds;
        if (Math.Abs(clock - stamp) > limit)
        {
            throw new WebhookSignatureException("stale",
                $"signed at {stamp}, {Math.Abs(clock - stamp).ToString("0", CultureInfo.InvariantCulture)} s from this clock");
        }
        var signed = new byte[Encoding.ASCII.GetByteCount(stamp.ToString(CultureInfo.InvariantCulture)) + 1 + body.Length];
        var head = Encoding.ASCII.GetBytes(stamp.ToString(CultureInfo.InvariantCulture) + ".");
        head.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(head.Length));
        foreach (var secret in secrets)
        {
            var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret.Reveal()), signed)));
            if (signatures.Any(given => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(given))))
            {
                return stamp;
            }
        }
        throw new WebhookSignatureException("mismatch", "no secret given made this signature");
    }

    /// <summary>As <see cref="Verify(ReadOnlySpan{byte}, string, IEnumerable{Secret}, TimeSpan?, DateTimeOffset?)"/>, for one secret and a body as text.</summary>
    /// <param name="body">The request body exactly as received, as UTF-8 text.</param>
    /// <param name="header">The <c>MFX-Signature</c> header.</param>
    /// <param name="secret">The endpoint's secret.</param>
    /// <param name="tolerance">How far the signed time may be from <paramref name="now"/>.</param>
    /// <param name="now">The receiver's clock.</param>
    /// <returns>The signed time, in unix seconds.</returns>
    public static long Verify(string body, string header, Secret secret, TimeSpan? tolerance = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Verify(Encoding.UTF8.GetBytes(body), header, [secret], tolerance, now);
    }

    private static (long Stamp, List<string> Signatures) Parts(string header)
    {
        long? stamp = null;
        var signatures = new List<string>();
        foreach (var item in header.Split(','))
        {
            var pair = item.Trim();
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var (key, value) = equals < 0 ? (pair, string.Empty) : (pair[..equals], pair[(equals + 1)..]);
            if (key == "t" && value.Length > 0 && value.All(char.IsAsciiDigit))
            {
                stamp = long.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (key == "v1" && value.Length > 0)
            {
                signatures.Add(value);
            }
        }
        return stamp is { } at && signatures.Count > 0
            ? (at, signatures)
            : throw new WebhookSignatureException("malformed", $"{SignatureHeader} needs t= and v1=: '{header}'");
    }

    /// <summary>
    /// Registers an HTTPS address for these event types (<c>position.closed</c>, <c>terminal.*</c>, or
    /// <c>*</c>). The answer carries the signing secret, shown here and never again: keep it first.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="eventTypes">The event types.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="description">What it is for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The endpoint, with its secret.</returns>
    public Task<WebhookEndpointCreated> CreateAsync(
        string url, IEnumerable<string> eventTypes, string idempotencyKey, string? description = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        var body = new WebhookEndpointCreate { Url = Address(url), EventTypes = [.. eventTypes], Description = description };
        return _client.WriteAsync<WebhookEndpointCreated>(HttpMethod.Post, "/webhooks", body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>Every endpoint of the tenant, each with how it is doing (delivered, retrying, dead).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The endpoints.</returns>
    public async Task<IReadOnlyList<WebhookEndpoint>> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = await _client.GetAsync<WebhookEndpointList>("/webhooks", null, cancellationToken).ConfigureAwait(false);
        return [.. list.Items ?? []];
    }

    /// <summary>One endpoint; another tenant's id is a 404.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The endpoint.</returns>
    public Task<WebhookEndpoint> GetAsync(Guid endpointId, CancellationToken cancellationToken = default) =>
        _client.GetAsync<WebhookEndpoint>(Path(endpointId), null, cancellationToken);

    /// <summary>Changes the address or the types, or pauses and resumes the endpoint. What is not given is left as it is.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="url">The new address.</param>
    /// <param name="eventTypes">The new event types.</param>
    /// <param name="description">The new description.</param>
    /// <param name="paused">True pauses it, false resumes it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The endpoint.</returns>
    public Task<WebhookEndpoint> UpdateAsync(
        Guid endpointId, string idempotencyKey, string? url = null, IEnumerable<string>? eventTypes = null, string? description = null,
        bool? paused = null, CancellationToken cancellationToken = default)
    {
        var body = new WebhookEndpointUpdate
        {
            Url = url is null ? null : Address(url),
            EventTypes = eventTypes?.ToList(),
            Description = description,
            Status = paused is { } pause ? (pause ? WebhookEndpointUpdateStatus.Paused : WebhookEndpointUpdateStatus.Active) : null,
        };
        return _client.WriteAsync<WebhookEndpoint>(HttpMethod.Patch, Path(endpointId), body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>Removes the endpoint and its secret; nothing more is sent to it. It takes no idempotency key, as in the contract.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>When it is done.</returns>
    public Task DeleteAsync(Guid endpointId, CancellationToken cancellationToken = default) =>
        _client.WriteAsync(HttpMethod.Delete, Path(endpointId), null, null, cancellationToken);

    /// <summary>A new secret, shown once; the previous one also signs until <c>previous_secret_until</c>.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new secret.</returns>
    public Task<WebhookSecretRotated> RotateSecretAsync(Guid endpointId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _client.WriteAsync<WebhookSecretRotated>(HttpMethod.Post, Path(endpointId, "/rotate-secret"), null, Keys.Required(idempotencyKey), cancellationToken);

    /// <summary>Asks for a <c>webhook.test</c> event at the endpoint, delivered and signed like any other. It takes no idempotency key.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The accepted test.</returns>
    public Task<WebhookTestAccepted> TestAsync(Guid endpointId, CancellationToken cancellationToken = default) =>
        _client.WriteAsync<WebhookTestAccepted>(HttpMethod.Post, Path(endpointId, "/test"), null, null, cancellationToken);

    /// <summary>
    /// What did not arrive: each failed event with every attempt on it, following the cursor to the end,
    /// narrowed to its states (sent as one comma-separated value).
    /// </summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="state">Retrying, dead (gave up) or delivered.</param>
    /// <param name="limit">The page size to ask for.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Every failure.</returns>
    public IAsyncEnumerable<WebhookFailure> FailuresAsync(
        Guid endpointId, IEnumerable<WebhookFailureState>? state = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var wanted = WireNames.Joined(state, nameof(state));
        return Pages.All((cursor, token) => _client.PageAsync<WebhookFailure>(Path(endpointId, "/failures"),
            new Query().Add("state", wanted).Add("cursor", cursor).Add("limit", limit), token), cancellationToken);
    }

    /// <summary>
    /// Sends events again: every one after a stream cursor (<paramref name="after"/>, an <c>MFX-Event-Id</c>),
    /// or one failure (<paramref name="failureId"/>); exactly one of the two.
    /// </summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="after">The event id to replay after.</param>
    /// <param name="failureId">The one failure to replay.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The accepted replay.</returns>
    public Task<WebhookReplayAccepted> ReplayAsync(
        Guid endpointId, string idempotencyKey, string? after = null, Guid? failureId = null, CancellationToken cancellationToken = default)
    {
        if ((after is null) == (failureId is null))
        {
            throw new ArgumentException("give exactly one of after and failureId");
        }
        var body = new WebhookReplay { After = after, FailureId = failureId };
        return _client.WriteAsync<WebhookReplayAccepted>(HttpMethod.Post, Path(endpointId, "/replay"), body, Keys.Required(idempotencyKey), cancellationToken);
    }

    /// <summary>The body's model takes a URI: an address that is not absolute cannot be written into it.</summary>
    private static Uri Address(string url, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(url))] string parameter = "") =>
        Uri.TryCreate(url, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
            ? address
            : throw new ArgumentException("the address is an absolute https URL", parameter);
}
