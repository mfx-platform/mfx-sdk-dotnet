using System.Net;
using System.Text;
using MfxHub.Models;
using Microsoft.Extensions.Logging;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The agreement refusals, typed (ServiceAgreementNotAcceptedException and TermsNotAcceptedException).
/// </summary>
public sealed class AgreementRefusalTests
{
    internal const string Refusal = """
        {"error": {"code": "service_agreement_not_accepted", "message": "Accept the Enterprise Service Agreement to add terminals",
         "details": {"plan_change": {"id": "0191f0c2-aaaa-7bbb-8ccc-000000000001", "from": "standard", "to": "enterprise",
                                     "state": "awaiting_acceptance", "requested_at": "2026-11-17T08:00:00Z",
                                     "until": "2026-12-01T08:00:00Z", "requested_by_operator": true},
                     "documents": [{"document": "service_agreement_enterprise", "number": 3, "plan": "enterprise", "grace_ends_at": null},
                                   {"document": "terms", "number": 7, "plan": null, "grace_ends_at": "2026-11-10T00:00:00Z"}],
                     "accept_url": "https://portal.mfxhub.com/organisation/legal"}}}
        """;

    /// <summary>
    /// Given a 409 service_agreement_not_accepted with its details / When a read fails with it / Then it is a
    /// ServiceAgreementNotAcceptedException, a ConflictException, whose plan change, documents and accept URL
    /// read as typed properties: the change by its contract model, each document with its plan and grace end.
    /// </summary>
    [Fact]
    public async Task A_service_agreement_refusal_is_typed_with_its_details()
    {
        using var api = new FakeApi().Answer(Refusal, 409);
        using var client = api.Client();

        var refused = await Assert.ThrowsAsync<ServiceAgreementNotAcceptedException>(() => client.Usage.GetAsync());

        Assert.IsAssignableFrom<ConflictException>(refused);
        Assert.Equal(("service_agreement_not_accepted", 409), (refused.Code, refused.Status));
        Assert.Equal(new Uri("https://portal.mfxhub.com/organisation/legal"), refused.AcceptUrl);
        var change = refused.PlanChange!;
        Assert.Equal((Guid.Parse(FakeApi.Id1), "standard", "enterprise", PlanChangeSummaryState.AwaitingAcceptance, true),
            (change.Id, change.From, change.To, change.State, change.RequestedByOperator));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), change.Until);
        Assert.Equal(
            [new DueDocument("service_agreement_enterprise", 3, "enterprise", null),
             new DueDocument("terms", 7, null, new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero))],
            refused.Documents);
    }

    /// <summary>
    /// Given a service agreement refusal with no plan change (a version past its grace period) and no other
    /// details / When it is read / Then the plan change and the accept URL are null and the documents empty:
    /// nothing is invented.
    /// </summary>
    [Fact]
    public async Task An_absent_plan_change_or_field_reads_as_null()
    {
        using var api = new FakeApi().Fail(409, "service_agreement_not_accepted", "accept it", """{"plan_change": null}""");
        using var client = api.Client();

        var refused = await Assert.ThrowsAsync<ServiceAgreementNotAcceptedException>(() => client.Usage.GetAsync());

        Assert.Null(refused.PlanChange);
        Assert.Null(refused.AcceptUrl);
        Assert.Empty(refused.Documents);
    }

    /// <summary>
    /// Given a 409 terms_not_accepted, until now an untyped conflict / When a write fails with it / Then it is
    /// a TermsNotAcceptedException, still a ConflictException, whose documents name each document, its version
    /// and when its grace period ended (the contract's grace_ends_at); it is not a service agreement refusal.
    /// </summary>
    [Fact]
    public async Task A_terms_refusal_is_typed_with_its_documents()
    {
        using var api = new FakeApi().Fail(409, "terms_not_accepted", "accept the terms",
            """{"documents": [{"document": "mt5_licence", "number": 2, "grace_ends_at": "2026-11-12T00:00:00Z"}]}""");
        using var client = api.Client();

        var refused = await Assert.ThrowsAsync<TermsNotAcceptedException>(
            () => client.Terminals.CreateAsync(Guid.Parse(FakeApi.Id1), Guid.Parse(FakeApi.Id2), "t-1"));

        Assert.IsAssignableFrom<ConflictException>(refused);
        Assert.IsNotType<ServiceAgreementNotAcceptedException>(refused);
        Assert.Equal([new DueDocument("mt5_licence", 2, null, new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero))], refused.Documents);
    }

    /// <summary>
    /// Given an organisation whose plan change awaits acceptance / When a terminal is asked for / Then the write
    /// throws ServiceAgreementNotAcceptedException and is sent once, never again.
    /// </summary>
    [Fact]
    public async Task A_write_refused_for_the_agreement_throws_and_is_sent_once()
    {
        using var api = new FakeApi().Answer(Refusal, 409);
        using var client = api.Client();

        await Assert.ThrowsAsync<ServiceAgreementNotAcceptedException>(
            () => client.Terminals.CreateAsync(Guid.Parse(FakeApi.Id1), Guid.Parse(FakeApi.Id2), "t-1"));

        Assert.Single(api.Requests);
    }
}

/// <summary>The tenant's own read of what it must accept.</summary>
public sealed class TenantServiceAgreementTests
{
    /// <summary>
    /// Given an organisation with a plan change awaiting acceptance / When client.Tenant.ServiceAgreementAsync
    /// is called / Then it is one GET on /v1/tenant/service-agreement, read as the contract's
    /// TenantServiceAgreement: due, held, the plan, the change, each document and the accept URL.
    /// </summary>
    [Fact]
    public async Task The_service_agreement_is_one_get_read_as_the_contract_model()
    {
        using var api = new FakeApi().Answer(FakeApi.R("""
            {"due": true, "held": true, "plan": "standard",
             "plan_change": {"id": "@Id1", "from": "standard", "to": "enterprise", "state": "awaiting_acceptance",
                             "requested_at": "2026-11-17T08:00:00Z", "until": "2026-12-01T08:00:00Z", "requested_by_operator": false},
             "documents": [{"document": "service_agreement_enterprise", "number": 1, "plan": "enterprise", "grace_ends_at": null, "held": true}],
             "accept_url": "https://portal.mfxhub.com/organisation/legal"}
            """));
        using var client = api.Client();

        var due = await client.Tenant.ServiceAgreementAsync();

        Assert.Equal(("GET", "/v1/tenant/service-agreement"), (api.Only.Method, api.Only.PathAndQuery));
        Assert.Equal((true, true, "standard"), (due.Due, due.Held, due.Plan));
        Assert.Equal(PlanChangeSummaryState.AwaitingAcceptance, due.PlanChange!.State);
        var document = Assert.Single(due.Documents);
        Assert.Equal(("service_agreement_enterprise", 1, "enterprise", true), (document.Document, document.Number, document.Plan, document.Held));
        Assert.Equal(new Uri("https://portal.mfxhub.com/organisation/legal"), due.AcceptUrl);
    }

    /// <summary>
    /// Given an organisation with nothing to accept / When the service agreement is read / Then due and held are
    /// false, the plan change null and the documents empty.
    /// </summary>
    [Fact]
    public async Task Nothing_due_reads_as_nothing_due()
    {
        using var api = new FakeApi().Answer("""
            {"due": false, "held": false, "plan": "enterprise", "plan_change": null, "documents": [],
             "accept_url": "https://portal.mfxhub.com/organisation/legal"}
            """);
        using var client = api.Client();

        var due = await client.Tenant.ServiceAgreementAsync();

        Assert.Equal((false, false), (due.Due, due.Held));
        Assert.Null(due.PlanChange);
        Assert.Empty(due.Documents);
    }
}

/// <summary>The one warning per client for <c>MFX-Service-Agreement: due</c>.</summary>
public sealed class ServiceAgreementWarningTests
{
    /// <summary>
    /// Given answers that carry MFX-Service-Agreement: due, a success and a failure among them / When one client
    /// makes several calls / Then its logger gets exactly one warning, the first time, naming
    /// Tenant.ServiceAgreementAsync; a second client warns once of its own.
    /// </summary>
    [Fact]
    public async Task One_warning_per_client_the_first_time_the_header_is_seen()
    {
        var due = ("MFX-Service-Agreement", "due");
        using var api = new FakeApi()
            .AnswerWith("""{"contacts": []}""", 200, due)
            .AnswerWith("""{"contacts": []}""", 200, due)
            .AnswerWith(AgreementRefusalTests.Refusal, 409, due)
            .AnswerWith("""{"contacts": []}""", 200, due);
        var log = new Recorded();
        var other = new Recorded();
        using var client = api.Client(log);
        using var second = api.Client(other);

        await client.Tenant.ContactsAsync();
        await client.Tenant.ContactsAsync();
        await Assert.ThrowsAsync<ServiceAgreementNotAcceptedException>(
            () => client.Terminals.CreateAsync(Guid.Parse(FakeApi.Id1), Guid.Parse(FakeApi.Id2), "t-1"));
        await second.Tenant.ContactsAsync();

        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("ServiceAgreementAsync", warning.Message, StringComparison.Ordinal);
        Assert.Single(other.Entries);
    }

    /// <summary>
    /// Given answers without the header, and a client with no logger / When calls are made, with and without
    /// the header / Then nothing is logged without it, and a client with no logger works as before.
    /// </summary>
    [Fact]
    public async Task No_header_no_warning_and_no_logger_is_harmless()
    {
        using var api = new FakeApi()
            .Answer("""{"contacts": []}""")
            .AnswerWith("""{"contacts": []}""", 200, ("MFX-Service-Agreement", "due"));
        var log = new Recorded();
        using var client = api.Client(log);
        using var silent = api.Client();

        await client.Tenant.ContactsAsync();
        await silent.Tenant.ContactsAsync();

        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// Given one client and a hundred concurrent calls whose every answer carries the header / When they all
    /// complete / Then exactly one warning was logged: the first sight is claimed once, atomically.
    /// </summary>
    [Fact]
    public async Task Concurrent_calls_still_warn_once()
    {
        var log = new Recorded();
        using var handler = new AlwaysDue();
        using var client = new MfxClient(new Uri("https://api.example.test/v1"), "mfxk_1", SigningKey.FromSeed(SigningTests.TestSeed),
            new MfxClientOptions { Handler = handler, Logger = log });

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => client.Tenant.ContactsAsync())));

        Assert.Single(log.Entries);
    }

    /// <summary>Answers every request at once, with the header, from any thread.</summary>
    private sealed class AlwaysDue : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"contacts": []}""", Encoding.UTF8, "application/json") };
            answer.Headers.TryAddWithoutValidation("MFX-Service-Agreement", "due");
            return Task.FromResult(answer);
        }
    }
}

/// <summary>A logger that keeps what it is told, from any thread.</summary>
internal sealed class Recorded : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
