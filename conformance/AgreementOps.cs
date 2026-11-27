using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MfxHub.Sdk.Conformance;

/// <summary>
/// The shared suite's plan-agreement operations (vocabulary.json, mfx-sdk-conformance 0.9.0) in .NET:
/// <c>tenant.service_agreement</c> and <c>agreement.due_warnings</c>, nothing more.
/// </summary>
internal static class AgreementOps
{
    public static bool Handles(string op) => op is "tenant.service_agreement" or "agreement.due_warnings";

    /// <param name="client">The case's client.</param>
    /// <param name="op">The operation.</param>
    /// <param name="args">Its arguments.</param>
    /// <param name="clientWith">A new client to the same hub, signing with the suite's key, logging to the logger given.</param>
    public static async Task<Dictionary<string, JsonNode?>> CallAsync(
        MfxClient client, string op, JsonObject args, Func<ILogger, MfxClient> clientWith)
    {
        switch (op)
        {
            case "tenant.service_agreement":
            {
                var due = await client.Tenant.ServiceAgreementAsync();
                var change = due.PlanChange is null ? null : JsonSerializer.SerializeToNode(due.PlanChange, MfxJson.Options);
                return new()
                {
                    ["due"] = due.Due,
                    ["held"] = due.Held,
                    ["plan"] = due.Plan,
                    ["plan_change_state"] = change?["state"]?.DeepClone(),
                    ["plan_change_to"] = change?["to"]?.DeepClone(),
                    ["documents"] = new JsonArray([.. due.Documents.Select(d => (JsonNode?)JsonValue.Create(d.Document))]),
                    ["accept_url"] = due.AcceptUrl?.OriginalString,
                };
            }
            case "agreement.due_warnings":
            {
                var terminal = Guid.Parse(args["terminal_id"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                var clients = args["clients"]!.GetValue<int>();
                var calls = args["calls"]!.GetValue<int>();
                var log = new CountingLogger();
                // One client after another, as the vocabulary says, each making its calls in turn.
                for (var i = 0; i < clients; i++)
                {
                    using var each = clientWith(log);
                    for (var j = 0; j < calls; j++)
                    {
                        await each.Terminals.GetAsync(terminal);
                    }
                }
                return new() { ["warnings"] = log.Warnings };
            }
            default:
                throw new InvalidOperationException("not an agreement operation: " + op);
        }
    }

    /// <summary>Counts the warnings it is told, from any thread.</summary>
    private sealed class CountingLogger : ILogger
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _warnings);
            }
        }
    }
}
