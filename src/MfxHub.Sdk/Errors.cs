using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using MfxHub.Models;

namespace MfxHub;

/// <summary>Every failure this SDK throws.</summary>
public class MfxException : Exception
{
    /// <summary>A failure with no message.</summary>
    public MfxException() { }

    /// <summary>A failure described by <paramref name="message"/>.</summary>
    public MfxException(string message) : base(message) { }

    /// <summary>A failure described by <paramref name="message"/>, caused by <paramref name="inner"/>.</summary>
    public MfxException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The request never got an answer: DNS, TCP, TLS, a dropped connection.</summary>
/// <remarks>
/// <see cref="MayHaveReached"/> is false only when the connection never opened, so nothing was
/// sent. A connection that dropped after the request was written may have delivered it, and an
/// order sent that way may already be a position.
/// </remarks>
public class MfxTransportException : MfxException
{
    /// <summary>A transport failure with no message; it may have reached the platform.</summary>
    public MfxTransportException() { }

    /// <summary>A transport failure; it may have reached the platform.</summary>
    public MfxTransportException(string message) : base(message) { }

    /// <summary>A transport failure caused by <paramref name="inner"/>; it may have reached the platform.</summary>
    public MfxTransportException(string message, Exception inner) : base(message, inner) { }

    /// <summary>A transport failure, saying whether the request could have arrived.</summary>
    public MfxTransportException(string message, bool mayHaveReached, Exception? inner = null)
        : base(message, inner!) => MayHaveReached = mayHaveReached;

    /// <summary>Whether the request could have arrived. False only when nothing was sent.</summary>
    public bool MayHaveReached { get; } = true;
}

/// <summary>The request got no answer in time. A write that timed out is never retried.</summary>
public class MfxTimeoutException : MfxTransportException
{
    /// <summary>A timeout with no message.</summary>
    public MfxTimeoutException() { }

    /// <summary>A timeout described by <paramref name="message"/>.</summary>
    public MfxTimeoutException(string message) : base(message) { }

    /// <summary>A timeout described by <paramref name="message"/>, caused by <paramref name="inner"/>.</summary>
    public MfxTimeoutException(string message, Exception inner) : base(message, true, inner) { }
}

/// <summary>MFX Hub answered, and the answer was a failure.</summary>
/// <remarks>
/// The platform's <c>error.code</c> decides the type; the HTTP status decides only when the code
/// is one this version does not know, so a code the platform adds later still arrives typed.
/// </remarks>
public class MfxApiException : MfxException
{
    private static readonly ReadOnlyDictionary<string, JsonElement> NoDetails = new(new Dictionary<string, JsonElement>());

    /// <summary>A failure with no description; prefer the constructor that names the code.</summary>
    public MfxApiException() : this(string.Empty, 0, nameof(MfxApiException)) { }

    /// <summary>A failure described by <paramref name="message"/>.</summary>
    public MfxApiException(string message) : this(message, 0, nameof(MfxApiException)) { }

    /// <summary>A failure described by <paramref name="message"/>, caused by <paramref name="inner"/>.</summary>
    public MfxApiException(string message, Exception inner) : base(message, inner)
    {
        Code = nameof(MfxApiException);
        Details = NoDetails;
    }

    /// <summary>A failure the platform described.</summary>
    /// <param name="message">The platform's sentence, as it was written.</param>
    /// <param name="status">The HTTP status; 0 for an error sent on the stream.</param>
    /// <param name="code">The platform's error code, as it arrived.</param>
    /// <param name="details">What the platform said about it, such as the field that was wrong.</param>
    /// <param name="retryAfter">How long the platform asked the caller to wait.</param>
    public MfxApiException(
        string message,
        int status,
        string code,
        IReadOnlyDictionary<string, JsonElement>? details = null,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details is null ? NoDetails : new ReadOnlyDictionary<string, JsonElement>(details.ToDictionary());
        RetryAfter = retryAfter;
    }

    /// <summary>The HTTP status; 0 for an error sent on the stream.</summary>
    public int Status { get; }

    /// <summary>The platform's error code, or the type's name when the answer carried none.</summary>
    public string Code { get; }

    /// <summary>What the platform said about the failure, read-only.</summary>
    public IReadOnlyDictionary<string, JsonElement> Details { get; }

    /// <summary>How long the platform asked the caller to wait, from <c>Retry-After</c>.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary><c>code (status): message</c>; <see cref="Exception.Message"/> is the sentence alone.</summary>
    public override string ToString() => $"{Code} ({Status}): {Message}";

    /// <summary>A text field of <see cref="Details"/>; null when absent or not text.</summary>
    protected string? DetailText(string name) =>
        Details.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A moment in <see cref="Details"/>, as an offset; null when absent or not one.</summary>
    protected DateTimeOffset? DetailTime(string name) =>
        DateTimeOffset.TryParse(DetailText(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}

/// <summary>The request cannot be taken as it was sent; <see cref="MfxApiException.Details"/> says which part.</summary>
public class ValidationException : MfxApiException
{
    /// <inheritdoc/>
    public ValidationException() { }

    /// <inheritdoc/>
    public ValidationException(string message) : base(message) { }

    /// <inheritdoc/>
    public ValidationException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public ValidationException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>What a Risk Management refusal names.</summary>
/// <param name="Reason">The refusal's reason, <c>risk_refused</c> when the platform named none.</param>
/// <param name="Rule">The rule that refused the entry.</param>
/// <param name="PolicyId">The policy whose rule refused it; null when a challenge's rule did.</param>
/// <param name="Version">The policy's version.</param>
/// <param name="ChallengeId">The challenge whose rule refused it; null when a policy's rule did.</param>
/// <param name="ChallengeVersion">The challenge's version.</param>
/// <param name="Used">The figure used so far, in the account's currency, as the platform wrote it.</param>
/// <param name="Limit">The rule's limit, as the platform wrote it.</param>
/// <param name="WouldBe">What the figure would have been, as the platform wrote it.</param>
public sealed record RiskRefusal(
    string Reason, string? Rule, string? PolicyId, int? Version, string? ChallengeId,
    int? ChallengeVersion, string? Used, string? Limit, string? WouldBe);

/// <summary>
/// A new entry broke a Risk Management rule in force on the account (422 <c>risk_refused</c>);
/// nothing was sent to the broker.
/// </summary>
public class RiskRefusedException : ValidationException
{
    /// <inheritdoc/>
    public RiskRefusedException() { }

    /// <inheritdoc/>
    public RiskRefusedException(string message) : base(message) { }

    /// <inheritdoc/>
    public RiskRefusedException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public RiskRefusedException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }

    /// <summary>What refused the entry, read from <see cref="MfxApiException.Details"/>.</summary>
    public RiskRefusal Refusal => new(
        Figure("reason") ?? "risk_refused", Figure("rule"), Figure("policy_id"), Whole("version"),
        Figure("challenge_id"), Whole("challenge_version"), Figure("used"), Figure("limit"), Figure("would_be"));

    private string? Figure(string name) => Details.TryGetValue(name, out var value)
        ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        }
        : null;

    private int? Whole(string name) => Details.TryGetValue(name, out var value)
        ? value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        }
        : null;
}

/// <summary>The request is not signed by a key the platform knows.</summary>
public class AuthException : MfxApiException
{
    /// <inheritdoc/>
    public AuthException() { }

    /// <inheritdoc/>
    public AuthException(string message) : base(message) { }

    /// <inheritdoc/>
    public AuthException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public AuthException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>The key may not do this.</summary>
public class ForbiddenException : MfxApiException
{
    /// <inheritdoc/>
    public ForbiddenException() { }

    /// <inheritdoc/>
    public ForbiddenException(string message) : base(message) { }

    /// <inheritdoc/>
    public ForbiddenException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public ForbiddenException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>
/// A change that relaxes protection, made without <c>risk:override</c> and a reason (403
/// <c>risk_override_required</c>). Nothing was changed.
/// </summary>
public class RiskOverrideRequiredException : ForbiddenException
{
    /// <inheritdoc/>
    public RiskOverrideRequiredException() { }

    /// <inheritdoc/>
    public RiskOverrideRequiredException(string message) : base(message) { }

    /// <inheritdoc/>
    public RiskOverrideRequiredException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public RiskOverrideRequiredException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>
/// The operator has disabled this tenant: every call is refused until it is enabled. Retrying
/// changes nothing; <see cref="Exception.Message"/> is what the operator wrote for the tenant.
/// </summary>
public class TenantDisabledException : ForbiddenException
{
    /// <inheritdoc/>
    public TenantDisabledException() { }

    /// <inheritdoc/>
    public TenantDisabledException(string message) : base(message) { }

    /// <inheritdoc/>
    public TenantDisabledException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public TenantDisabledException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }

    /// <summary>When the tenant was disabled; null if the platform did not say.</summary>
    public DateTimeOffset? Since => DetailTime("since");
}

/// <summary>
/// The operator has suspended this tenant: writes are refused now, reads until
/// <see cref="ReadsUntil"/>. Not retryable: it ends when the operator resumes the tenant.
/// </summary>
public class TenantSuspendedException : ForbiddenException
{
    /// <inheritdoc/>
    public TenantSuspendedException() { }

    /// <inheritdoc/>
    public TenantSuspendedException(string message) : base(message) { }

    /// <inheritdoc/>
    public TenantSuspendedException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public TenantSuspendedException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }

    /// <summary>When the suspension began; null if the platform did not say.</summary>
    public DateTimeOffset? Since => DetailTime("since");

    /// <summary>When reads stop working too; null if the platform named no grace.</summary>
    public DateTimeOffset? ReadsUntil => DetailTime("reads_until");
}

/// <summary>
/// A planned maintenance window suspends this until <see cref="EndsAt"/> (403 <c>maintenance</c>).
/// Send it again when the window ends, with the same idempotency key for a write.
/// </summary>
public class MaintenanceException : ForbiddenException
{
    /// <inheritdoc/>
    public MaintenanceException() { }

    /// <inheritdoc/>
    public MaintenanceException(string message) : base(message) { }

    /// <inheritdoc/>
    public MaintenanceException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public MaintenanceException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }

    /// <summary>The window's id.</summary>
    public string? WindowId => DetailText("window_id");

    /// <summary>The window's label as people say it, such as <c>MW-0031</c>.</summary>
    public string? Window => DetailText("window");

    /// <summary>The window's current end; null if the platform did not say. It can move.</summary>
    public DateTimeOffset? EndsAt => DetailTime("ends_at");

    /// <summary>The suspended service that refused this call, such as <c>order_placement</c>.</summary>
    public string? Service => DetailText("service");

    /// <summary>Every service the window suspends.</summary>
    public IReadOnlyList<string> Suspended =>
        Details.TryGetValue("suspended", out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!)]
            : [];
}

/// <summary>No such thing; also the answer for another tenant's id.</summary>
public class NotFoundException : MfxApiException
{
    /// <inheritdoc/>
    public NotFoundException() { }

    /// <inheritdoc/>
    public NotFoundException(string message) : base(message) { }

    /// <inheritdoc/>
    public NotFoundException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public NotFoundException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>The request conflicts with what already exists.</summary>
public class ConflictException : MfxApiException
{
    /// <inheritdoc/>
    public ConflictException() { }

    /// <inheritdoc/>
    public ConflictException(string message) : base(message) { }

    /// <inheritdoc/>
    public ConflictException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public ConflictException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>The thing exists and is not in a state this can be done in.</summary>
public class StateException : ConflictException
{
    /// <inheritdoc/>
    public StateException() { }

    /// <inheritdoc/>
    public StateException(string message) : base(message) { }

    /// <inheritdoc/>
    public StateException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public StateException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>The terminal cannot be placed.</summary>
public class PlacementException : ConflictException
{
    /// <inheritdoc/>
    public PlacementException() { }

    /// <inheritdoc/>
    public PlacementException(string message) : base(message) { }

    /// <inheritdoc/>
    public PlacementException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public PlacementException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>
/// MFX Hub does not support this broker or this server now. <see cref="RefusedBy"/> is
/// <c>permission</c> (this broker is not available) or <c>server_disabled</c> (try another server).
/// </summary>
public class UnsupportedBrokerException : ConflictException
{
    /// <inheritdoc/>
    public UnsupportedBrokerException() { }

    /// <inheritdoc/>
    public UnsupportedBrokerException(string message) : base(message) { }

    /// <inheritdoc/>
    public UnsupportedBrokerException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public UnsupportedBrokerException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }

    /// <summary><c>permission</c> or <c>server_disabled</c>; null if the platform did not say.</summary>
    public string? RefusedBy => DetailText("refused_by");

    /// <summary>The platform's own reason code.</summary>
    public string? Reason => DetailText("reason");

    /// <summary>The broker the refusal concerns, when the platform names one.</summary>
    public string? BrokerId => DetailText("broker_id");

    /// <summary>The broker server the refusal concerns, when the platform names one.</summary>
    public string? ServerId => DetailText("server_id");
}

/// <summary>The key is over its budget; <see cref="MfxApiException.RetryAfter"/> is the platform's own answer.</summary>
public class RateLimitException : MfxApiException
{
    /// <inheritdoc/>
    public RateLimitException() { }

    /// <inheritdoc/>
    public RateLimitException(string message) : base(message) { }

    /// <inheritdoc/>
    public RateLimitException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public RateLimitException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>The tenant's terminal or broker did not answer.</summary>
public class UpstreamException : MfxApiException
{
    /// <inheritdoc/>
    public UpstreamException() { }

    /// <inheritdoc/>
    public UpstreamException(string message) : base(message) { }

    /// <inheritdoc/>
    public UpstreamException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public UpstreamException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>
/// A store of the platform's own did not answer; the request was valid, and waiting
/// <see cref="MfxApiException.RetryAfter"/> is enough.
/// </summary>
public class DependencyUnavailableException : UpstreamException
{
    /// <inheritdoc/>
    public DependencyUnavailableException() { }

    /// <inheritdoc/>
    public DependencyUnavailableException(string message) : base(message) { }

    /// <inheritdoc/>
    public DependencyUnavailableException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public DependencyUnavailableException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>An answer from the network's edge, not from MFX Hub (a 502 or 504 with no MFX code).</summary>
public class EdgeException : UpstreamException
{
    /// <inheritdoc/>
    public EdgeException() { }

    /// <inheritdoc/>
    public EdgeException(string message) : base(message) { }

    /// <inheritdoc/>
    public EdgeException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public EdgeException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>MFX Hub failed, and it is not the caller's request that is wrong.</summary>
public class InternalException : MfxApiException
{
    /// <inheritdoc/>
    public InternalException() { }

    /// <inheritdoc/>
    public InternalException(string message) : base(message) { }

    /// <inheritdoc/>
    public InternalException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public InternalException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>
/// 402: a payment is missing. Not retryable unchanged: it ends when someone pays.
/// <see cref="BillingPaymentRequiredException"/> is the one the platform sends with an API key; a 402
/// whose code this version does not know arrives as this class, by its status.
/// </summary>
public class PaymentRequiredException : MfxApiException
{
    /// <inheritdoc/>
    public PaymentRequiredException() { }

    /// <inheritdoc/>
    public PaymentRequiredException(string message) : base(message) { }

    /// <inheritdoc/>
    public PaymentRequiredException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public PaymentRequiredException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }
}

/// <summary>
/// What clears a billing standing, read from a <see cref="BillingPaymentRequiredException"/>'s details
/// (the contract's <c>BillingAction</c>, with its amount as an exact decimal).
/// </summary>
/// <param name="Kind"><c>top_up</c> or <c>pay_invoice</c>.</param>
/// <param name="PortalPath">The portal page that does it, relative to the portal's address (<c>/billing/top-up</c>).</param>
/// <param name="AmountUsd">At least what clears it, exact; null when not said.</param>
/// <param name="InvoiceId">The invoice to pay; null for a top-up.</param>
[ContractSchema("BillingAction")]
public sealed record PaymentAction(string Kind, string PortalPath, decimal? AmountUsd, string? InvoiceId);

/// <summary>
/// 402 <c>billing_payment_required</c>: a payment is missing on the billing profile the request falls
/// under, and nothing was done. Not retryable: sending the same request again changes nothing until
/// the payment is made.
/// </summary>
/// <remarks>
/// At <see cref="Stage"/> <c>reminder</c> it comes only from a request that adds something billed; once
/// <c>suspended</c> it can come from any write on the tenant or the profile, whatever route it was sent
/// to. Reads keep working throughout, the billing reads included. Show <see cref="Exception.Message"/>
/// and <see cref="Action"/> to a person; once paid, send the request again (a write under the same
/// idempotency key). Execution methods never throw: an order refused for billing comes back as a refused
/// <see cref="ExecutionResult"/> whose <see cref="ExecutionResult.Detail"/> carries this error's text.
/// The error's <c>cause</c> is <see cref="BillingCause"/> here, so it cannot be read as an exception's own
/// cause (<see cref="Exception.InnerException"/>).
/// </remarks>
public class BillingPaymentRequiredException : PaymentRequiredException
{
    /// <inheritdoc/>
    public BillingPaymentRequiredException() { }

    /// <inheritdoc/>
    public BillingPaymentRequiredException(string message) : base(message) { }

    /// <inheritdoc/>
    public BillingPaymentRequiredException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public BillingPaymentRequiredException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }

    /// <summary>The tenant the refusal concerns.</summary>
    public string? TenantId => Blank(DetailText("tenant_id"));

    /// <summary>The billing profile the request falls under; on Standard, its one profile.</summary>
    public string? BillingProfileId => Blank(DetailText("billing_profile_id"));

    /// <summary><c>reminder</c> (only what adds something billed is refused) or <c>suspended</c> (every write on the profile is).</summary>
    public string? Stage => DetailText("stage");

    /// <summary>Why: <c>balance_used_up</c> or <c>invoice_overdue</c> (the error's <c>cause</c>).</summary>
    public string? BillingCause => DetailText("cause");

    /// <summary>When the stage began; null if the platform did not say.</summary>
    public DateTimeOffset? Since => DetailTime("since");

    /// <summary>The first suspended moment; null if the platform did not say.</summary>
    public DateTimeOffset? SuspendsAt => DetailTime("suspends_at");

    /// <summary>The last moment a payment avoids the suspension; null if not said.</summary>
    public DateTimeOffset? PayBy => DetailTime("pay_by");

    /// <summary>What clears it, with the portal page that does it; null if the platform named none.</summary>
    public PaymentAction? Action
    {
        get
        {
            if (!Details.TryGetValue("action", out var raw) || raw.ValueKind != JsonValueKind.Object
                || !raw.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || !raw.TryGetProperty("portal_path", out var path) || path.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            decimal? amount = raw.TryGetProperty("amount_usd", out var figure) && figure.ValueKind == JsonValueKind.String
                && decimal.TryParse(figure.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var exact)
                ? exact
                : null;
            var invoice = raw.TryGetProperty("invoice_id", out var id) && id.ValueKind == JsonValueKind.String ? Blank(id.GetString()) : null;
            return new PaymentAction(kind.GetString()!, path.GetString()!, amount, invoice);
        }
    }

    private static string? Blank(string? text) => string.IsNullOrEmpty(text) ? null : text;
}

/// <summary>
/// One legal document an organisation must accept, read from the details of a
/// <see cref="ServiceAgreementNotAcceptedException"/> or a <see cref="TermsNotAcceptedException"/>.
/// </summary>
/// <param name="Document">
/// The document's key: <c>terms</c>, <c>privacy</c>, <c>mt5_licence</c> or a plan's Service Agreement,
/// <c>service_agreement_&lt;plan&gt;</c>. The list is open: a key this version does not know is one more document.
/// </param>
/// <param name="Number">The version to accept.</param>
/// <param name="Plan">The plan a Service Agreement belongs to; null for a document common to every plan.</param>
/// <param name="GraceEndsAt">
/// When its grace period ends or ended; null for a plan change's agreement, which waits for acceptance instead.
/// </param>
public sealed record DueDocument(string Document, int Number, string? Plan, DateTimeOffset? GraceEndsAt)
{
    /// <summary>The documents of an agreement refusal's details; empty when it names none.</summary>
    internal static IReadOnlyList<DueDocument> From(IReadOnlyDictionary<string, JsonElement> details)
    {
        if (!details.TryGetValue("documents", out var raw) || raw.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var documents = new List<DueDocument>();
        foreach (var item in raw.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("document", out var key) || key.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("number", out var number) || number.ValueKind != JsonValueKind.Number
                || !number.TryGetInt32(out var version))
            {
                continue;
            }
            var grace = Time(item, "grace_ends_at");
            var plan = item.TryGetProperty("plan", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
            documents.Add(new DueDocument(key.GetString()!, version, string.IsNullOrEmpty(plan) ? null : plan, grace));
        }
        return documents;
    }

    private static DateTimeOffset? Time(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}

/// <summary>
/// 409 <c>terms_not_accepted</c>: a version of the Terms of Service or the MetaTrader 5 licence that the
/// organisation must accept is past its grace period unaccepted, and nothing was done. Adding a terminal and
/// attaching an expert advisor are refused until an owner or admin accepts it; nothing running is touched.
/// Not retryable unchanged: send the request again once it is accepted.
/// </summary>
public class TermsNotAcceptedException : ConflictException
{
    /// <inheritdoc/>
    public TermsNotAcceptedException() { }

    /// <inheritdoc/>
    public TermsNotAcceptedException(string message) : base(message) { }

    /// <inheritdoc/>
    public TermsNotAcceptedException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public TermsNotAcceptedException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }

    /// <summary>Each document due, its version and when its grace period ended; empty when the platform named none.</summary>
    public IReadOnlyList<DueDocument> Documents => DueDocument.From(Details);
}

/// <summary>
/// 409 <c>service_agreement_not_accepted</c>: the organisation is moving to another plan and the new plan's
/// Service Agreement has not been accepted yet, or a version of its plan's Service Agreement that must be
/// accepted is past its grace period unaccepted. Nothing was done. Not retryable unchanged.
/// </summary>
/// <remarks>
/// Adding a terminal, attaching an expert advisor, generating or replacing an API key and asking for higher
/// limits are refused until an owner or admin accepts it at <see cref="AcceptUrl"/>; reads, orders, positions
/// and anything running are never refused. Where the Terms are also held, this is the error that answers, and
/// <see cref="Documents"/> lists every document due. <see cref="Tenant.ServiceAgreementAsync"/> says what is due.
/// </remarks>
public class ServiceAgreementNotAcceptedException : ConflictException
{
    /// <inheritdoc/>
    public ServiceAgreementNotAcceptedException() { }

    /// <inheritdoc/>
    public ServiceAgreementNotAcceptedException(string message) : base(message) { }

    /// <inheritdoc/>
    public ServiceAgreementNotAcceptedException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc/>
    public ServiceAgreementNotAcceptedException(string message, int status, string code,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
        : base(message, status, code, details, retryAfter) { }

    /// <summary>The plan change awaiting acceptance; null when the refusal is for a version past its grace period.</summary>
    public PlanChangeSummary? PlanChange
    {
        get
        {
            if (!Details.TryGetValue("plan_change", out var raw) || raw.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            try
            {
                return raw.Deserialize<PlanChangeSummary>(MfxJson.Options);
            }
            catch (JsonException)
            {
                // A change this version cannot read stays in Details, as it arrived.
                return null;
            }
        }
    }

    /// <summary>Each document and version due, with its plan and grace end; empty when the platform named none.</summary>
    public IReadOnlyList<DueDocument> Documents => DueDocument.From(Details);

    /// <summary>Where an owner or admin accepts, in the customer portal; null if the platform did not say.</summary>
    public Uri? AcceptUrl => Uri.TryCreate(DetailText("accept_url"), UriKind.Absolute, out var url) ? url : null;
}

/// <summary>One mapping from the platform's description of a failure to its type.</summary>
internal static class ApiErrors
{
    private delegate MfxApiException Make(
        string message, int status, string code, IReadOnlyDictionary<string, JsonElement>? details, TimeSpan? retryAfter);

    private sealed record Kind(Type Type, Make New);

    private static Kind Of<T>(Make make) where T : MfxApiException => new(typeof(T), make);

    /// <summary>By the platform's code.</summary>
    internal static readonly IReadOnlyDictionary<string, Type> ByCodeTypes;

    /// <summary>By the HTTP status, when the code is unknown.</summary>
    internal static readonly IReadOnlyDictionary<int, Type> ByStatusTypes;

    private static readonly Dictionary<string, Kind> ByCode = new(StringComparer.Ordinal)
    {
        ["validation_error"] = Of<ValidationException>((m, s, c, d, r) => new ValidationException(m, s, c, d, r)),
        ["auth_error"] = Of<AuthException>((m, s, c, d, r) => new AuthException(m, s, c, d, r)),
        ["forbidden"] = Of<ForbiddenException>((m, s, c, d, r) => new ForbiddenException(m, s, c, d, r)),
        ["tenant_disabled"] = Of<TenantDisabledException>((m, s, c, d, r) => new TenantDisabledException(m, s, c, d, r)),
        ["tenant_suspended"] = Of<TenantSuspendedException>((m, s, c, d, r) => new TenantSuspendedException(m, s, c, d, r)),
        ["maintenance"] = Of<MaintenanceException>((m, s, c, d, r) => new MaintenanceException(m, s, c, d, r)),
        ["not_found"] = Of<NotFoundException>((m, s, c, d, r) => new NotFoundException(m, s, c, d, r)),
        ["conflict"] = Of<ConflictException>((m, s, c, d, r) => new ConflictException(m, s, c, d, r)),
        ["state_error"] = Of<StateException>((m, s, c, d, r) => new StateException(m, s, c, d, r)),
        ["placement_error"] = Of<PlacementException>((m, s, c, d, r) => new PlacementException(m, s, c, d, r)),
        ["risk_refused"] = Of<RiskRefusedException>((m, s, c, d, r) => new RiskRefusedException(m, s, c, d, r)),
        ["risk_override_required"] = Of<RiskOverrideRequiredException>((m, s, c, d, r) => new RiskOverrideRequiredException(m, s, c, d, r)),
        ["broker_not_supported"] = Of<UnsupportedBrokerException>((m, s, c, d, r) => new UnsupportedBrokerException(m, s, c, d, r)),
        ["rate_limited"] = Of<RateLimitException>((m, s, c, d, r) => new RateLimitException(m, s, c, d, r)),
        ["upstream_error"] = Of<UpstreamException>((m, s, c, d, r) => new UpstreamException(m, s, c, d, r)),
        // 503 as well, told apart by its code alone: a store of the platform's own, with
        // Retry-After, where upstream_error is the tenant's terminal or broker.
        ["dependency_unavailable"] = Of<DependencyUnavailableException>((m, s, c, d, r) => new DependencyUnavailableException(m, s, c, d, r)),
        ["internal_error"] = Of<InternalException>((m, s, c, d, r) => new InternalException(m, s, c, d, r)),
        // 402, and about neither the key nor the request: a payment is missing on the billing profile.
        ["billing_payment_required"] = Of<BillingPaymentRequiredException>((m, s, c, d, r) => new BillingPaymentRequiredException(m, s, c, d, r)),
        // 409, about the organisation's legal standing: additions are held until a document is accepted.
        ["terms_not_accepted"] = Of<TermsNotAcceptedException>((m, s, c, d, r) => new TermsNotAcceptedException(m, s, c, d, r)),
        ["service_agreement_not_accepted"] = Of<ServiceAgreementNotAcceptedException>((m, s, c, d, r) => new ServiceAgreementNotAcceptedException(m, s, c, d, r)),
    };

    private static readonly Dictionary<int, Kind> ByStatus = new()
    {
        [400] = ByCode["validation_error"],
        [401] = ByCode["auth_error"],
        [402] = Of<PaymentRequiredException>((m, s, c, d, r) => new PaymentRequiredException(m, s, c, d, r)),
        [403] = ByCode["forbidden"],
        [404] = ByCode["not_found"],
        [409] = ByCode["conflict"],
        [422] = ByCode["validation_error"],
        [429] = ByCode["rate_limited"],
        [500] = ByCode["internal_error"],
        // The platform answers neither 502 nor 504; either is the edge's own page, reached only
        // without an MFX code.
        [502] = Of<EdgeException>((m, s, c, d, r) => new EdgeException(m, s, c, d, r)),
        [503] = ByCode["upstream_error"],
        [504] = Of<EdgeException>((m, s, c, d, r) => new EdgeException(m, s, c, d, r)),
    };

    static ApiErrors()
    {
        ByCodeTypes = ByCode.ToDictionary(p => p.Key, p => p.Value.Type);
        ByStatusTypes = ByStatus.ToDictionary(p => p.Key, p => p.Value.Type);
    }

    /// <summary>
    /// The typed error for a failure the platform described, on a call's answer or the stream. The
    /// code decides; the status only when the code is one this version does not know.
    /// </summary>
    internal static MfxApiException For(
        string code, string message, int status,
        IReadOnlyDictionary<string, JsonElement>? details = null, TimeSpan? retryAfter = null)
    {
        if (ByCode.TryGetValue(code, out var kind) || ByStatus.TryGetValue(status, out kind))
        {
            return kind.New(message, status, code.Length > 0 ? code : kind.Type.Name, details, retryAfter);
        }
        return new MfxApiException(message, status, code.Length > 0 ? code : nameof(MfxApiException), details, retryAfter);
    }

    /// <summary>
    /// The typed error for a failed answer; null for a successful one. <paramref name="what"/> is
    /// the call in the caller's words, used when the body carries no message of its own.
    /// </summary>
    internal static MfxApiException? For(MfxAnswer answer, string what, TimeSpan? retryAfter)
    {
        if (answer.Status is >= 200 and < 400)
        {
            return null;
        }
        var code = string.Empty;
        var message = string.Empty;
        Dictionary<string, JsonElement>? details = null;
        try
        {
            using var body = JsonDocument.Parse(answer.Body);
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                code = Text(error, "code");
                message = Text(error, "message");
                if (error.TryGetProperty("details", out var raw) && raw.ValueKind == JsonValueKind.Object)
                {
                    details = raw.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                }
            }
        }
        catch (JsonException)
        {
            // An answer that is not JSON (an edge's HTML page) is still a typed failure, by status.
        }
        return For(code, message.Length > 0 ? message : $"{what} failed", answer.Status, details, retryAfter);
    }

    private static string Text(JsonElement error, string name) =>
        error.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText())
            : string.Empty;
}
