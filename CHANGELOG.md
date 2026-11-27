# Changelog

Every release of `MfxHub.Sdk`, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). Each change is derived from the Python SDK; the module
and the commit it derives from are named.

## Unreleased

## 0.1.0

Enriched: the domain clients, in eight pull requests.

### Added

- `docs/sdkref.json`, the SDK reference in format `mfx-sdkref/1`, written by `tools/SdkRef` from the
  public API (reflection over the built assembly, C# signatures with nullability) and the XML documentation as
  Markdown; each domain method names the contract operation it calls, derived from its route, and each exception
  the error code it maps. CI regenerates it, fails on drift and runs the shared suite's checker, now vendored at
  0.10.1 with its `sdkref` folder.
- In `docs/sdkref.json`, each type that represents one contract object names it in `schema` (the key of
  `components.schemas`), which the docs site links to the object's fields: the stream events and the records read
  from one object, and the generated models a listed signature names, now listed on the page that first names
  them, without members. `tools/generate.ps1` marks each model made from a schema with its key, internally; the
  shared suite is vendored at 0.11.0 (`70ce249`), and CI runs its checker with `--spec spec/openapi.yaml`, so a
  schema the contract lacks fails the build.

### Changed

- The comment-limit `ArgumentException` reads "comment is at most 31 characters; the terminal would cut a longer one
  (N given)", and the public doc comments say "the terminal" where they named the platform's internals.

### Fixed

- An answer carrying an enum word this version was not generated with no longer fails the call with a
  `JsonException`. Every generated enum the contract writes as words has `UnknownValue`
  (-1, no wire word), and the word reads as it, in a property, a list or a map key, with `MfxJson.Options` or
  System.Text.Json's defaults; a newer SDK names the word. A request refuses `UnknownValue` before anything is
  sent: as a filter (`ArgumentOutOfRangeException`) and in a body (`JsonException`). Outside a request an
  answer still serialises, writing `UnknownValue`, which reads back as itself. It is `UnknownValue`, not
  `Unknown`, because eleven contract enums already have the word `unknown`. Shared suite 0.13.0 (`5e57a41`).
- `DueDocument.GraceEndsAt` reads `grace_ends_at` only, when the grace period ends or ended, for both agreement
  refusals. The contract renamed `terms_not_accepted`'s `grace_ended_at` to it and the platform sends only
  `grace_ends_at`, so the fallback to the old name is gone, as in Python. The shared suite is vendored
  at 0.12.0 (`d492672`), whose Terms case sends `grace_ends_at`.
- `ClosePositionAsync` and `ClosePositionPartialAsync`, on `Execution` and `TradingAccounts`, take an optional
  `comment` for the closing deal (`Deal.comment`; the contract's `PositionClose` and `PositionClosePartial`),
  as Python's closes do (51860d8). Absent, none is sent and the deal takes the position's own. It is
  checked as an order's: over 31 code points throws the same `ArgumentException` before anything is sent.
  Close-by takes none: its body has no comment.
- An order's comment over 31 characters, counted in Unicode code points as the contract's maxLength counts,
  is refused with `ArgumentException` ("comment is at most 31 characters; MT5 would cut a longer one (N given)")
  before anything is sent, on `Execution.PlaceOrderAsync` and `TradingAccounts.PlaceOrderAsync`, as Python
  refuses it (e0b76c7). The doc comment said the platform refused it.

### Added

- **Plan agreements** (as Python's
  `ServiceAgreementNotAccepted`, `TermsNotAccepted` and `tenant.service_agreement()`):
  `ServiceAgreementNotAcceptedException` for 409 `service_agreement_not_accepted`, with `PlanChange` (the
  contract's `PlanChangeSummary`; null for a version past its grace period), `Documents` (each a
  `DueDocument`: document, version, plan, grace end) and `AcceptUrl`; `TermsNotAcceptedException` for 409
  `terms_not_accepted`, until now an untyped `ConflictException`, with `Documents`. Both are
  `ConflictException`s, so a catch of that still catches them.
- `client.Tenant.ServiceAgreementAsync()` over `GET /tenant/service-agreement`: a `TenantServiceAgreement`
  saying what is due, whether additions are held, the plan change and where to accept.
- `MfxClientOptions.Logger`, an `ILogger` (Microsoft.Extensions.Logging.Abstractions): the first answer
  that carries `MFX-Service-Agreement: due` logs one warning, once per client and never again on it, however
  many calls run at once. No logger, no warning.
- The models from the contract at 9af2676 (`PlanChangeSummary`, `TenantServiceAgreement`), the commit Python's
  spec names. `tools/surface-check/map.json` gains `ahead`: the two error codes this SDK maps before the
  vendored `surface.json` has them, removed when it is next re-vendored.
- The shared suite 0.9.0 (7d27547): its eight plan-agreement cases and the typed agreement errors pass, with
  `tenant.service_agreement` and `agreement.due_warnings` in the runner (`conformance/AgreementOps.cs`).
- **Billing** (`client.Billing`, Python `billing.py`, PR !97): `PriceListAsync` (needs no key, and
  is signed as every request is), `SummaryAsync`, `ProfileAsync`, `ForecastAsync` (a month as
  `YYYY-MM`), `PricesAsync`, `InvoiceAsync`, `InvoicesPageAsync` and `InvoicesAsync`, by billing
  profile and invoice state; amounts stay the contract's exact decimal text.
- `PaymentRequiredException`, the class for any 402, and beneath it
  `BillingPaymentRequiredException` for `billing_payment_required`: `TenantId`,
  `BillingProfileId`, `Stage`, `BillingCause` (Python's `cause`; named so it cannot be read as an
  exception's own cause, `InnerException`), `Since`, `SuspendsAt`, `PayBy` and `Action`, a
  `PaymentAction` whose `AmountUsd` is an exact `decimal`. A write refused for billing throws it; an
  order returns a refused `ExecutionResult` with its text in `Detail`, and never throws.
- The surface check's `pending` is empty: every entry of Python's `surface.json` maps.
- **Copy Trading** (`client.Copy`, Python `copy_trading.py`, b3bad52, PR !92): leaders
  (`CreateLeaderAsync`, `GetLeaderAsync`, `LeadersAsync`, `AllLeadersAsync`, `UpdateLeaderAsync`,
  `PauseLeaderAsync`, `ResumeLeaderAsync`, `RetireLeaderAsync`, and an external leader's
  `SendOpenSignalAsync`, exactly one of `volume` and `units`, and `SendCloseSignalAsync`);
  followers (`CreateFollowerAsync`, `PreviewFollowerAsync`, a read with no key, `GetFollowerAsync`,
  `FollowersAsync`, `AllFollowersAsync`, `UpdateFollowerAsync`, `PauseFollowerAsync`,
  `ResumeFollowerAsync`, `ResyncFollowerAsync`, `UnfollowAsync`) and their stop-outs
  (`StopoutsAsync`, `AllStopoutsAsync`, `ResetStopoutsAsync`); groups (`CreateGroupAsync`,
  `GetGroupAsync`, `GroupsAsync`, `AllGroupsAsync`, `UpdateGroupAsync` with `Change<T>` to clear a
  field, `AddGroupFollowersAsync`, `RemoveGroupFollowersAsync`, `PauseGroupAsync`,
  `ResumeGroupAsync`, `CloseGroupAsync`, `RolloverGroupAsync`); requested closes (`ClosesAsync`,
  `AllClosesAsync`, `GetCloseAsync`); the copy log (`LogAsync`, `AllLogAsync`); signals
  (`SignalsAsync`, `AllSignalsAsync`); and the tenant's stop-outs (`TenantStopoutsAsync`,
  `AllTenantStopoutsAsync`). A nested setting is checked against the contract before sending: a
  name it does not have, a word outside its list, or a `double`, is an `ArgumentException`.
  `Copy.RefusalReasons`, `CopyLogSort`, `CopyRolloverField`.
- The `copy.*` events from `MfxEvent.Of`: `CopyCopied`, `CopyRefused` (with its `RiskRefusal`),
  `CopyStopoutEvent`, `CopyDrift`, `CopyStateChanged` and `CopyClosedEvent`, with `CopyDeal`,
  `CopyAs`, `CopyClosed` and `CopyNotClosed`.
- **Webhooks** (`client.Webhooks`, Python `webhooks.py`, b3bad52): `CreateAsync` (the secret comes
  back once), `ListAsync`, `GetAsync`, `UpdateAsync` (only what is given; `paused`),
  `DeleteAsync`, `RotateSecretAsync`, `TestAsync`, `FailuresAsync` and `ReplayAsync` (exactly one
  of `after` and `failureId`). `Webhooks.Verify` checks a delivery's `MFX-Signature` against one
  secret or several (a rotation), in constant time and within a tolerance, and throws
  `WebhookSignatureException` with its `Reason` (`malformed`, `stale`, `mismatch`).
- **Expert advisors** (`client.ExpertAdvisors`, Python `expert_advisors.py`): `UploadAsync` (each
  file's source as UTF-8 in base64), `GetAsync`, `PageAsync`, `ListAsync`, `ReportAsync`,
  `DeleteAsync` and `WaitForVerdictAsync`; `ExpertAdvisors.TelemetryOf` (`EaTelemetry`,
  `EaRecord`, `EaTelemetryLost`). **Tenant** (`client.Tenant`): `ContactsAsync`.
- **Risk Management** (`client.Risk`, Python `risk.py`, b3bad52): policies (`CreatePolicyAsync`,
  `AddPolicyVersionAsync`, `ArchivePolicyAsync`, `ActivateAsync`, `PolicyAsync`,
  `PolicyVersionAsync`, `PoliciesAsync`, `PolicyVersionsAsync`); protected accounts
  (`AccountsPageAsync` with its stats, `AccountsAsync`, `AccountAsync`, `EquityAsync`,
  `CloseScopeAsync`, `DeactivateAsync`, `ClearAsync`, `CoverageAsync`); the planner
  (`PreviewAsync`, `CheckAsync`, reads with no key); exit plans (`CreatePlanAsync`, `PlanAsync`,
  `PlansPageAsync`, `PlansAsync`, `AmendPlanAsync`, `PausePlanAsync`, `ResumePlanAsync`,
  `EndPlanAsync`); challenges (`CreateChallengeAsync`, `AddChallengeVersionAsync`,
  `ArchiveChallengeAsync`, `ChallengeAsync`, `ChallengesAsync`, `EnrolAsync`,
  `ChallengeAccountsPageAsync`, `ChallengeAccountsAsync`, `ChallengeAccountAsync`); and activity
  (`ActivityPageAsync`, `ActivityAsync`). Bodies are the generated models or anything in the
  contract's shape; a decimal goes as text and a `double` anywhere in one is refused before
  sending. Several values of a filter go as repeated parameters, and a sort is always sent.
  `CoverageHealth`, `RiskAccountSort`, `TimeSort`, `RiskActivitySort`, `RiskChallengeRule` and
  `RiskChallengeSort` name the filters the contract has no model enum for.
- The risk events from `MfxEvent.Of`: `RiskRuleEvent` (`risk.rule_warning`, `risk.rule_breached`,
  `risk.rule_cleared`), `RiskCoverageChanged`, `RiskProgrammeResult` and `RiskPeriodClosed`.
- The shared suite's `risk` area passes: `conformance/not-yet.json` is empty.
- **Market data** (`client.Md`, a `MarketData`, Python `md.py`, b3bad52): `SubscribeAsync` (with
  candle timeframes), `SubscriptionsAsync`, `AllSubscriptionsAsync`, `SubscribeAccountAsync` (a
  cTrader account's spots, pending until the first), `AccountSubscriptionsAsync`,
  `AllAccountSubscriptionsAsync`, `UnsubscribeAsync`, `CandlesAsync`, `AccountCandlesAsync`,
  `TicksAsync` and `AllTicksAsync` (every tick once across a shared millisecond); a timeframe that
  is not one of the nine is refused before sending. `MarketData.BookOf` (`Book`, exact levels) and
  `MarketData.SubscriptionEvent` (`SubscriptionDegraded`, `SubscriptionRecovered`).
- **Typed stream events** (Python `events.py`): `MfxEvent.Of(message)` reads `OrderPlaced`,
  `OrderModified`, `OrderClosed`, `PositionOpened`, `PositionModified`, `PositionClosed`,
  `TerminalStateChanged` and `CandleClosed` (read by its `candles:` channel), ids as `Guid`,
  tickets as `long`, money as `decimal`; another type is null, and a missing required field a
  `FormatException` naming it. `MfxEvent.Kinds` lists what it reads.
- The shared suite's `md` area passes.

- **Trading accounts** (`client.TradingAccounts`, Python `trading_accounts.py`, b3bad52): `GetAsync`,
  `PageAsync` and `ListAsync` (by venue, environment and state), `FundsAsync`, `PositionsAsync`,
  `PendingOrdersAsync`, `DealsAsync` (a window, optionally one position) and `SymbolsAsync`; the
  account's commands (`PlaceOrderAsync` to `ClearTrailingStopAsync`), the same bodies and rules as
  `client.Execution` under `/accounts/{id}`, each an `ExecutionResult`; and the cTrader grant flow:
  `StartCtraderGrantAsync`, `CtraderGrantAsync`, `ConnectCtraderAccountsAsync` and
  `RevokeCtraderGrantAsync`.
- **`LiveAccount`** and `AccountView` (Python `live_account.py`): one terminal's account followed on
  `account:{terminal_id}`, snapshot then marks, by the stream's ordering rules; a message it cannot
  apply is dropped whole and `Behind` goes true until a snapshot.

- **Execution** (`client.Execution`, Python `execution.py` and `results.py`, b3bad52):
  `PlaceOrderAsync`, `CancelOrderAsync`, `ModifyPositionAsync`, `ModifyOrderAsync` (its expiry
  kept, cleared or set through `Expiry`), `ClosePositionAsync`, `ClosePositionByAsync`,
  `ClosePositionPartialAsync`, `SetTrailingStopAsync`, `ClearTrailingStopAsync`, `GetCommandAsync`
  and `WaitForAsync`. Each returns an `ExecutionResult` and never throws for an outcome: a refusal,
  a rate limit, a maintenance window (retryable at `RetryAt`), a timeout (never retryable) and a
  lost connection (UNKNOWN, not retryable) are results. A final duplicate is read once and returned
  as the original. Every amount is a `decimal`, sent as plain digits with its scale; a negative one
  is refused before sending, and no public method takes a `double`.
- `ExecutionResult`, `Reason`, `Tickets`, `VenueDeal`, `Side` and `OrderType`; the command's state is
  the contract's `MfxHub.Models.CommandState`.
- **Accounts** (`client.Accounts`, Python `accounts.py`): `AccountAsync`, `PositionsAsync`,
  `PendingOrdersAsync`, `DealsAsync` (a required window) and `SpreadAsync`.
- The shared suite's `decimals`, `idempotency` and `execution` areas pass: the runner has
  `execution.place_order`, `catalog.quote` and `terminals.create`, and checks a refusal.

- **Terminals, the rest beyond Core** (Python `terminals.py`, b3bad52): `CreateAsync`,
  `JournalAsync` (1 to 200 lines, checked here), `SubmitCredentialsAsync` (the password is a
  `Secret`), `RestartAsync`, `ReplaceInstrumentsAsync`, `AttachExpertAdvisorAsync`,
  `DetachExpertAdvisorAsync`, `ConfirmExpertAdvisorAsync`, `GetOperationAsync`,
  `WaitForOperationAsync`, `WaitUntilActiveAsync` (a QUEUED terminal is read again at its expected
  start), `BulkRestartAsync` (checked before it is sent), `GetBulkActionAsync`,
  `CancelBulkActionAsync`, `DrainAsync`, `ResumeAsync` and `RetireAsync`; `Terminals.IsUsable`,
  `OnItsWay`, `OperationFinal` and `OfflineIsTemporary`. Every write takes the caller's
  idempotency key, refuses a blank one before sending, and is sent once.
- **Catalog** (`client.Catalog`, Python `catalog.py`): `BrokersAsync`, `AllBrokersAsync`,
  `ServersAsync`, `CataloguesAsync`, `SymbolsAsync`, `SymbolAsync` and `QuoteAsync`, the symbol in
  the broker's own name as one escaped path segment; prices stay exact decimal text.
- **Farms** (`client.Farms`), **usage** (`client.Usage`, a `UsageReader`) and **maintenance**
  (`client.Maintenance`: `PageAsync`, `ListAsync`, `TerminalsPageAsync`, `TerminalsAsync` and
  `Maintenance.WindowEvent` for `maintenance.window_changed`).
- `Secret`: a value that prints as `***` and refuses to be serialised (Python `secrets.py`).
- The surface check covers Enriched: every resource is a property of the client, every method is
  async with a `CancellationToken` last, and every event a public type with its fields. What has
  not landed is held in `tools/surface-check/map.json`'s `pending`, each entry naming its Task.

### Fixed

- A list or map of the contract's enums (a farm's `usages`, a window's `suspended`, a
  subscription's `timeframes`) is read and written as the contract's words: NSwag puts its string
  converter on enum properties only, so such a list was written as numbers and could not be read.
  `MfxJson.Options` now converts every enum that carries wire names; an enum the contract writes as
  a number (a farm's `refresh_ms`) stays a number.

### Changed

- The contract is re-vendored: `spec/openapi.yaml` is the contract's `api/openapi.public.yaml` at `ff54f71`,
  the commit Python's `spec/SOURCE` names (recorded in `spec/openapi.SOURCE`), and the models are
  regenerated from it. `spec/surface.json` is Python's at `b3bad52` (0.18.0 and Unreleased).
