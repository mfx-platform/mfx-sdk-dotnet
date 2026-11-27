# MFX Hub .NET SDK

The typed .NET client for the [MFX Hub](https://mfxhub.com) API: hosted trading terminals for your
own broker accounts, with market data, execution, account state and a live event stream behind one
signed REST and WebSocket API.

```bash
dotnet add package MfxHub.Sdk
```

.NET 10 or later. Async throughout, nullable-annotated, and every release passes the same shared
test suite as the Python and TypeScript SDKs.

## Your first quote

You need an API key (its id, and the private key shown once when it was issued), your broker's
name, and your server's name exactly as your trading platform's login window shows it. A quote is
read from one of your terminals on that server, so ask for one first, in the
[portal](https://portal.mfxhub.com) or with `client.Terminals`.

```csharp
using MfxHub;

var api = new Uri("https://api.dev.mfxhub.com/v1"); // https://api.mfxhub.com/v1 for production
var key = SigningKey.Parse(Environment.GetEnvironmentVariable("MFX_PRIVATE_KEY")!); // PEM, or base64
using var client = new MfxClient(api, Environment.GetEnvironmentVariable("MFX_KEY_ID")!, key);

// Every request below is signed with your key; the key itself is never sent, logged or printed.
// 1. The ids for the names you know: your broker, and your server.
var brokerName = Environment.GetEnvironmentVariable("MFX_BROKER")!;
MfxHub.Models.TenantBroker? broker = null; // the models are in MfxHub.Models
await foreach (var b in client.Catalog.AllBrokersAsync()) // follows the cursor, page by page
{
    if (string.Equals(b.Code, brokerName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(b.DisplayName, brokerName, StringComparison.OrdinalIgnoreCase))
    {
        broker = b;
        break;
    }
}
var servers = await client.Catalog.ServersAsync(broker!.Id);
var server = servers.First(s => s.ServerName == Environment.GetEnvironmentVariable("MFX_SERVER"));

// 2. A quote, always with its age. Prices are exact decimal text.
var quote = await client.Catalog.QuoteAsync(broker.Id, "EURUSD", server.Id);
Console.WriteLine($"{quote.Bid} / {quote.Ask}, {quote.AgeS}s old, market {quote.State}");
```

Run it with `MFX_KEY_ID`, `MFX_PRIVATE_KEY`, `MFX_BROKER` (for example `Pepperstone`) and
`MFX_SERVER` (for example `Pepperstone-Demo`) set. Use a **demo** account while you try the SDK: an
order is real on whatever account the terminal is logged in to. The
[knowledge base](https://github.com/mfx-platform/mfx-kb) walks through a first order step by step.

## Reference

The [SDK documentation](https://docs.mfxhub.com/sdk) has guides and the full reference for every
client: terminals, execution, accounts and the catalogue, market data, the stream, Risk Management,
Copy Trading, billing and errors. The API itself is described at [docs.mfxhub.com](https://docs.mfxhub.com).

## What to know

- **Every write takes an `idempotencyKey`, and none defaults it.** Retrying with the same key
  returns the original answer; a fresh key asks for a second terminal or a second order.
- **Execution methods never throw** for what the platform or the network does. A transport fault, a
  timeout, a rate limit and a broker rejection all come back as an `ExecutionResult` with a granular
  reason, the broker's own comment and whether a retry may help. A result in state `UNKNOWN` may
  already be a position: never re-send it under a new key; the platform reconciles it.
- **Prices, volumes and money are exact.** You pass `decimal`; the SDK sends and reads decimal
  text, so `0.07 * 3` is never `0.21000000000000002`.
- **Reads throw typed exceptions**, mapped from the API's `error.code`: `NotFoundException`,
  `RateLimitException` with the time to wait, `UpstreamException` (with
  `DependencyUnavailableException` and `EdgeException` beneath it), `MaintenanceException` with the
  window's end, and the rest, all `MfxApiException`s.
- **Risk Management refuses an entry with its reason.** An order a rule refuses throws
  `RiskRefusedException`, which names the rule, its limit and the figures. Closes, partial closes
  and cancels are never refused.
- **Credentials cannot be logged by accident.** `SigningKey` and `Secret` render as `***` in every
  `ToString`, log line and exception, and refuse to be serialised.
- **A word from a newer API reads, it does not fail.** When an answer carries an enum word this
  version was not built with (a close's `Cause`, a terminal's `State`), the field reads as that enum's
  `UnknownValue`; a newer SDK names it. A request refuses `UnknownValue` before anything is sent.
- **Lists follow the cursor.** There are no totals and no page numbers; a method that returns an
  `IAsyncEnumerable<T>` walks every page, one request at a time, and one that returns a `Page<T>`
  returns one.
- **What your organisation must accept.** While a plan change, a Service Agreement or the Terms wait
  to be accepted, additions (a terminal, an expert advisor, an API key, higher limits) throw
  `ServiceAgreementNotAcceptedException` or `TermsNotAcceptedException`; nothing running is refused.
  `client.Tenant.ServiceAgreementAsync()` says what is due, and the first answer that says
  `MFX-Service-Agreement: due` logs one warning to the `ILogger` in `MfxClientOptions.Logger`.
- **The stream** (`await client.StreamAsync()`) carries market data, candles, the order book,
  account state, results and events on one WebSocket, resumed per channel after a reconnect.

## How this package is built

The models in `src/MfxHub.Sdk/Generated` are generated from the published MFX Hub API description in
`spec/openapi.yaml` by `tools/generate.ps1`, and are never edited by hand; CI regenerates them and
fails on any difference. The hand-written clients around them (`MfxClient`, `Terminals`, `Execution`,
`Catalog` and the rest) are what you use.

```bash
dotnet build -c Release            # warnings are errors
dotnet test -c Release             # the unit tests and, with MFX_SUITE_PYTHON set, the shared suite
pwsh tools/generate.ps1            # after the API description changes
```

[CONTRIBUTING.md](CONTRIBUTING.md) has the rest.

## Support and security

- Documentation: [docs.mfxhub.com/sdk](https://docs.mfxhub.com/sdk).
- Questions and bugs: open an issue, or write to support@mfxhub.com.
- Contributing: see [CONTRIBUTING.md](CONTRIBUTING.md); every release is in [CHANGELOG.md](CHANGELOG.md).
- Security reports: see [SECURITY.md](SECURITY.md). Please do not open a public issue for one.

## Licence and trademarks

Licensed under the [Apache License 2.0](LICENSE).

MFX Hub is an independent service and is not affiliated with, endorsed by or sponsored by
MetaQuotes Ltd. MetaTrader is a registered trademark of MetaQuotes Ltd., used here only to say which
trading platform MFX Hub's terminals are compatible with.
