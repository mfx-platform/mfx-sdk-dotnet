using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MfxHub.Sdk.Conformance;

/// <summary>
/// The .NET runner of the shared SDK test suite. It holds no rule: it asks the fake hub for each
/// case, turns the case's neutral operation into the .NET call, and checks what came back against
/// the case and against the hub's verdict on what was sent.
/// </summary>
public sealed class SuiteTests(Hub hub) : IClassFixture<Hub>
{
    private static readonly JsonObject Vocabulary = Hub.Read("suite/vocabulary.json").AsObject();

    /// <summary>Cases this SDK does not pass yet, each with the step or phase that will claim it.</summary>
    private static readonly IReadOnlyDictionary<string, string> NotYet =
        Hub.Read("not-yet.json").AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());

    private static readonly byte[] Seed = Convert.FromHexString(Vocabulary["key"]!["seed_hex"]!.GetValue<string>());

    private static IEnumerable<JsonObject> Cases() =>
        Directory.GetFiles(Path.Combine(Hub.Suite, "cases"), "*.json")
            .Order(StringComparer.Ordinal)
            .SelectMany(file => JsonNode.Parse(File.ReadAllText(file))!.AsArray().Select(c => c!.AsObject()));

    /// <summary>Every case id of the vendored suite, for the theory below.</summary>
    public static TheoryData<string> CaseIds()
    {
        var ids = new TheoryData<string>();
        foreach (var c in Cases())
        {
            ids.Add(c["id"]!.GetValue<string>());
        }
        return ids;
    }

    /// <summary>The not-yet list names only cases the suite has, so it cannot go stale.</summary>
    [Fact]
    public void The_not_yet_list_names_only_cases_the_suite_has()
    {
        var ids = Cases().Select(c => c["id"]!.GetValue<string>()).ToHashSet();
        Assert.Empty(NotYet.Keys.Where(id => !ids.Contains(id)));
    }

    /// <summary>
    /// Given a case of the shared SDK test suite, played back by the suite's fake MFX Hub / When this
    /// SDK makes the case's call, signed with the suite's test key / Then it returns, throws or
    /// refuses exactly as the case observes, and the hub's verdict is that every expected request
    /// arrived in order, signed and shaped as the case says; a case listed as not yet passed must
    /// fail, so the list cannot hide a case that passes.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task The_dotnet_sdk_passes_the_case(string id)
    {
        var problems = await RunAsync(id);

        if (NotYet.TryGetValue(id, out var why))
        {
            Assert.True(problems.Count > 0, $"{id} passes: take it off not-yet.json ({why})");
        }
        else
        {
            Assert.Empty(problems);
        }
    }

    private async Task<List<string>> RunAsync(string id)
    {
        var problems = new List<string>();
        var c = await hub.LoadAsync(id);
        using var client = new MfxClient(hub.BaseUrl, Vocabulary["key"]!["id"]!.GetValue<string>(),
            SigningKey.FromSeed(Seed), new MfxClientOptions { Pause = (_, _) => Task.CompletedTask });
        var call = c["call"]?.AsObject();
        var observe = c["observe"]!.AsObject();
        try
        {
            if (call is null)
            {
                throw new NotYetException("a case without a call");
            }
            var op = call["op"]!.GetValue<string>();
            var args = call["args"]!.AsObject();
            if (observe["raises"] is JsonValue raises)
            {
                var kind = Kind(raises.GetValue<string>());
                Exception? thrown = null;
                try
                {
                    await CallAsync(client, op, args);
                }
                catch (NotYetException)
                {
                    throw;
                }
#pragma warning disable CA1031 // the runner reports whatever the SDK threw, as a problem
                catch (Exception failure)
#pragma warning restore CA1031
                {
                    thrown = failure;
                }
                if (thrown is null)
                {
                    problems.Add($"returned, where {kind.Name} was expected");
                }
                else if (!kind.IsInstanceOfType(thrown))
                {
                    problems.Add($"threw {thrown.GetType().Name} ({thrown.Message}), where {kind.Name} was expected");
                }
                else
                {
                    foreach (var (name, want) in observe["fields"]?.AsObject() ?? [])
                    {
                        var value = JsonSerializer.SerializeToNode(thrown.GetType().GetProperty(Pascal(name))?.GetValue(thrown));
                        if (!Same(value, want))
                        {
                            problems.Add($"{name}: got {value?.ToJsonString() ?? "null"}, want {want?.ToJsonString()}");
                        }
                    }
                }
            }
            else if (observe["returns"] is JsonObject returns)
            {
                var got = await CallAsync(client, op, args);
                foreach (var (name, want) in returns)
                {
                    if (!Same(got.GetValueOrDefault(name), want))
                    {
                        problems.Add($"{name}: got {got.GetValueOrDefault(name)?.ToJsonString() ?? "null"}, want {want?.ToJsonString()}");
                    }
                }
            }
            else if (observe["refuses"] is JsonValue refuses)
            {
                // invalid_argument is the language's argument or type error: in .NET an
                // ArgumentException, or the compiler's refusal of a double where a decimal goes,
                // which the runner stands in for (Refused) because such a call cannot be written.
                Exception? thrown = null;
                try
                {
                    await CallAsync(client, op, args);
                }
                catch (NotYetException)
                {
                    throw;
                }
#pragma warning disable CA1031 // the runner reports whatever the SDK threw, as a problem
                catch (Exception failure)
#pragma warning restore CA1031
                {
                    thrown = failure;
                }
                if (refuses.GetValue<string>() != "invalid_argument")
                {
                    problems.Add($"the runner cannot check a refusal of '{refuses.GetValue<string>()}'");
                }
                else if (thrown is not ArgumentException and not TypeRefusedException)
                {
                    problems.Add(thrown is null ? "returned, where a refusal was expected" : $"threw {thrown.GetType().Name} ({thrown.Message}), where an argument error was expected");
                }
            }
            else
            {
                problems.Add($"the runner cannot check '{string.Join(",", observe.Select(p => p.Key))}' yet");
            }
        }
        catch (NotYetException missing)
        {
            problems.Add($"the SDK has no {missing.Message} yet");
        }
        problems.AddRange(await hub.ProblemsAsync());
        return problems;
    }

    /// <summary>The suite's neutral error kind as this SDK's type: <c>FooError</c> is <c>FooException</c>.</summary>
    private static Type Kind(string neutral) => neutral switch
    {
        "ApiError" => typeof(MfxApiException),
        "TransportError" => typeof(MfxTransportException),
        "Timeout" => typeof(MfxTimeoutException),
        _ => typeof(MfxApiException).Assembly.GetType("MfxHub." + neutral[..^"Error".Length] + "Exception")
            ?? throw new InvalidOperationException("the SDK has no error kind " + neutral),
    };

    private static string Pascal(string snake) =>
        string.Concat(snake.Split('_').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>Makes the case's call through this SDK and describes what came back in the suite's names.</summary>
    private static async Task<Dictionary<string, JsonNode?>> CallAsync(MfxClient client, string op, JsonObject args)
    {
        string Text(string name) => args[name]!.GetValue<string>();
        string[] Channels() => [.. args["channels"]!.AsArray().Select(c => c!.GetValue<string>())];

        switch (op)
        {
            case "signing.sign":
                var message = Signing.Message(
                    Text("timestamp"), Text("nonce"), Text("method"), Text("path_with_query"),
                    Encoding.UTF8.GetBytes(Text("body")));
                return new() { ["signature"] = SigningKey.FromSeed(Seed).Sign(message) };
            case "terminals.get":
                // Read back in the API's own JSON, so the state is the contract's word (ACTIVE).
                var terminal = JsonSerializer.SerializeToNode(
                    await client.Terminals.GetAsync(Guid.Parse(Text("terminal_id"), CultureInfo.InvariantCulture)),
                    MfxJson.Options)!;
                return new() { ["id"] = terminal["id"]?.DeepClone(), ["state"] = terminal["state"]?.DeepClone() };
            case "terminals.page":
                var page = await client.Terminals.PageAsync(
                    cursor: args["cursor"]?.GetValue<string>(), limit: args["limit"]?.GetValue<int>());
                return new() { ["count"] = page.Count, ["next_cursor"] = page.NextCursor };
            case "terminals.list":
                var ids = new JsonArray();
                await foreach (var listed in client.Terminals.ListAsync(limit: args["limit"]?.GetValue<int>()))
                {
                    ids.Add(listed.Id.ToString("D", CultureInfo.InvariantCulture));
                }
                return new() { ["ids"] = ids };
            case "terminals.create":
                var created = JsonSerializer.SerializeToNode(
                    await client.Terminals.CreateAsync(
                        Id(args, "broker_id"), Id(args, "server_id"), Text("idempotency_key"),
                        farmId: args["farm_id"] is null ? null : Id(args, "farm_id")),
                    MfxJson.Options)!;
                return new() { ["id"] = created["id"]?.DeepClone(), ["state"] = created["state"]?.DeepClone() };
            case "execution.place_order":
                var result = await client.Execution.PlaceOrderAsync(
                    Id(args, "terminal_id"), Text("symbol"), Text("side") == "buy" ? Side.Buy : Side.Sell,
                    Exact(args["volume"]), Text("idempotency_key"));
                return new()
                {
                    ["ok"] = result.Ok,
                    ["state"] = result.State is { } state ? WireWord(state) : null,
                    ["command_id"] = result.CommandId?.ToString("D", CultureInfo.InvariantCulture),
                    ["duplicate"] = result.Duplicate,
                    ["retryable"] = result.Retryable,
                    ["exec_price"] = result.ExecPrice?.ToString(CultureInfo.InvariantCulture),
                    ["position"] = result.Tickets.Position,
                    ["comment_raw"] = result.CommentRaw,
                };
            case "catalog.quote":
                var quote = await client.Catalog.QuoteAsync(
                    Id(args, "broker_id"), Text("symbol"), serverId: args["server_id"] is null ? null : Id(args, "server_id"));
                return new() { ["bid"] = quote.Bid, ["ask"] = quote.Ask, ["age_s"] = quote.AgeS, ["state"] = WireWord(quote.State) };
            case "md.subscribe_account":
                var subscription = JsonSerializer.SerializeToNode(
                    await client.Md.SubscribeAccountAsync(Id(args, "account_id"), Text("symbol"), Text("idempotency_key")), MfxJson.Options)!;
                return new()
                {
                    ["id"] = subscription["id"]?.DeepClone(),
                    ["account_id"] = subscription["account_id"]?.DeepClone(),
                    ["symbol"] = subscription["symbol"]?.DeepClone(),
                    ["state"] = subscription["state"]?.DeepClone(),
                };
            case "md.account_subscriptions":
                var symbols = new JsonArray();
                await foreach (var each in client.Md.AllAccountSubscriptionsAsync(Id(args, "account_id")))
                {
                    symbols.Add(each.Symbol);
                }
                return new() { ["symbols"] = symbols };
            case "md.account_candles":
                var bars = (await client.Md.AccountCandlesAsync(
                    Id(args, "account_id"), Text("symbol"), Text("timeframe"), Instant(Text("from")), Instant(Text("to")))).Items.ToList();
                return new()
                {
                    ["count"] = bars.Count,
                    ["first_open"] = bars.Count > 0 ? bars[0].O : null,
                    ["last_close"] = bars.Count > 0 ? bars[^1].C : null,
                };
            case "risk.check":
                var check = await client.Risk.CheckAsync(Id(args, "account_id"), Order(args));
                return new() { ["verdict"] = JsonSerializer.SerializeToNode(check, MfxJson.Options)!["verdict"]?.DeepClone() };
            case "risk.create_plan":
                var plan = new Dictionary<string, object?>
                {
                    ["account_id"] = Text("account_id"),
                    ["order"] = Order(args),
                    ["steps"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["trigger"] = new Dictionary<string, object?> { ["kind"] = "r", ["value"] = "1" },
                            ["action"] = new Dictionary<string, object?> { ["kind"] = "close_part", ["pct"] = "50" },
                            ["held_by"] = "mfx",
                        },
                    },
                };
                var placed = await client.Risk.CreatePlanAsync(plan, Text("idempotency_key"));
                return new() { ["id"] = placed.Id.ToString("D", CultureInfo.InvariantCulture) };
            case "risk.create_policy":
                var policy = JsonSerializer.SerializeToNode(
                    await client.Risk.CreatePolicyAsync(Text("name"), args["rules"]!.DeepClone(), args["reset"]!.DeepClone(), Text("idempotency_key")),
                    MfxJson.Options)!;
                return new() { ["id"] = policy["id"]?.DeepClone(), ["version"] = policy["current_version"]?["version"]?.DeepClone() };
            case "risk.challenge_accounts":
                var enrolled = new JsonArray();
                var states = args["state"]!.AsArray().Select(v => Word<MfxHub.Models.RiskChallengeAccountState>(v!.GetValue<string>()));
                await foreach (var account in client.Risk.ChallengeAccountsAsync(
                    Id(args, "challenge_id"), state: states, sort: Word<RiskChallengeSort>(Text("sort"))))
                {
                    enrolled.Add(account.AccountId.ToString("D", CultureInfo.InvariantCulture));
                }
                return new() { ["ids"] = enrolled };
            case "stream.read":
                return await ReadStreamAsync(client, Channels(), args["count"]!.GetValue<int>());
            case "stream.close_waiting":
                return await CloseWaitingAsync(client, Channels());
            default:
                if (AgreementOps.Handles(op))
                {
                    return await AgreementOps.CallAsync(client, op, args, logger => new MfxClient(
                        client.BaseUrl, client.KeyId, SigningKey.FromSeed(Seed),
                        new MfxClientOptions { Pause = (_, _) => Task.CompletedTask, Logger = logger }));
                }
                return CopyOps.Handles(op) ? await CopyOps.CallAsync(client, op, args) : throw new NotYetException(op);
        }
    }

    /// <summary>
    /// Opens the stream (reconnecting allowed, no real wait between attempts), subscribes each channel
    /// in order, reads until <paramref name="count"/> messages have arrived, then closes.
    /// </summary>
    private static async Task<Dictionary<string, JsonNode?>> ReadStreamAsync(MfxClient client, string[] channels, int count)
    {
        var seqs = new JsonArray();
        var types = new JsonArray();
        var stream = await client.StreamAsync(new StreamOptions { MaxReconnects = 3 });
        await using (stream)
        {
            foreach (var channel in channels)
            {
                await stream.SubscribeAsync(channel);
            }
            await foreach (var streamEvent in stream)
            {
                if (streamEvent is StreamMessage message)
                {
                    seqs.Add(message.Seq);
                    types.Add(message.Type);
                    if (seqs.Count >= count)
                    {
                        break;
                    }
                }
            }
        }
        var stats = stream.Stats;
        return new()
        {
            ["seqs"] = seqs,
            ["types"] = types,
            ["dropped"] = stats.Dropped,
            ["reconnects"] = stats.Reconnects,
            ["lifetime_reconnects"] = stats.LifetimeReconnects,
            ["slow_consumer_drops"] = stats.SlowConsumerDrops,
            ["resyncs"] = stats.Resyncs,
            ["refused"] = stats.Refused,
        };
    }

    /// <summary>
    /// Leaves a read waiting on a quiet stream, closes the stream, and sees whether the read ended: it
    /// has when it finishes within 2 s with nothing, no message and no error.
    /// </summary>
    private static async Task<Dictionary<string, JsonNode?>> CloseWaitingAsync(MfxClient client, string[] channels)
    {
        var stream = await client.StreamAsync(new StreamOptions { MaxReconnects = 3 });
        foreach (var channel in channels)
        {
            await stream.SubscribeAsync(channel);
        }
        var waiting = stream.NextAsync();
        await Task.Delay(100);
        await stream.CloseAsync();
        var ended = await Task.WhenAny(waiting, Task.Delay(2000)) == waiting
            && waiting.IsCompletedSuccessfully && waiting.Result is null;
        await stream.DisposeAsync();
        return new() { ["ended"] = ended, ["reconnects"] = stream.Stats.Reconnects };
    }

    /// <summary>A case value against what came back: a <c>$decimal</c> compares as a decimal.</summary>
    private static bool Same(JsonNode? got, JsonNode? want)
    {
        if (want is JsonObject { } wrapped && wrapped["$decimal"] is { } exact)
        {
            return got is not null
                && decimal.TryParse(got.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var left)
                && decimal.TryParse(exact.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var right)
                && left == right;
        }
        // By value, as the Java runner compares: an object's keys in any order, a number by its value
        // whatever its width (2 and 2.0), lists in order.
        return (got, want) switch
        {
            (null, null) => true,
            (JsonObject g, JsonObject w) => g.Count == w.Count && w.All(p => g.ContainsKey(p.Key) && Same(g[p.Key], p.Value)),
            (JsonArray g, JsonArray w) => g.Count == w.Count && g.Zip(w).All(pair => Same(pair.First, pair.Second)),
            (JsonValue g, JsonValue w) when g.GetValueKind() == JsonValueKind.Number && w.GetValueKind() == JsonValueKind.Number =>
                decimal.TryParse(g.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                && decimal.TryParse(w.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var right) && left == right,
            _ => (got?.ToJsonString() ?? "null") == (want?.ToJsonString() ?? "null"),
        };
    }

    private static DateTimeOffset Instant(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static Guid Id(JsonObject args, string name) => Guid.Parse(args[name]!.GetValue<string>(), CultureInfo.InvariantCulture);

    /// <summary>
    /// A case's decimal as the language's: a <c>$decimal</c> is a <see cref="decimal"/>, read exactly
    /// (an exponent such as 1E+3 included). A <c>$float</c> cannot be passed at all: a double does not
    /// convert to a decimal implicitly, so the compiler refuses the call, and the runner stands in for
    /// that refusal before anything is sent.
    /// </summary>
    internal static decimal Exact(JsonNode? value)
    {
        if (value is JsonObject wrapped && wrapped["$decimal"] is { } text)
        {
            return decimal.Parse(text.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        if (value is JsonObject binary && binary["$float"] is not null)
        {
            throw new TypeRefusedException("a double does not convert to decimal: the call does not compile");
        }
        return decimal.Parse(value!.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A market order already sized, as a mapping in the contract's shape. A <c>$decimal</c> volume is a
    /// <see cref="decimal"/>; a <c>$float</c> one is a <see cref="double"/>, which a mapping can hold, so
    /// the SDK itself must refuse it.
    /// </summary>
    private static Dictionary<string, object?> Order(JsonObject args) => new()
    {
        ["symbol"] = args["symbol"]!.GetValue<string>(),
        ["side"] = args["side"]!.GetValue<string>(),
        ["type"] = "market",
        ["volume"] = args["volume"] is JsonObject { } v && v["$float"] is { } f ? f.GetValue<double>() : Exact(args["volume"]),
    };

    /// <summary>The enum value whose contract word is <paramref name="word"/>.</summary>
    /// <remarks>A word the enum does not have cannot be passed at all: it is the language's argument error.</remarks>
    internal static TEnum Word<TEnum>(string word)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Where(v => WireWord(v) == word).Select(v => (TEnum?)v).FirstOrDefault()
            ?? throw new ArgumentException($"'{word}' is not a {typeof(TEnum).Name}");

    /// <summary>An enum value as the contract writes it, through the API's own JSON.</summary>
    private static string WireWord<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        typeof(TEnum).GetField(value.ToString())?.GetCustomAttribute<System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute>()?.Name
            ?? value.ToString();

    private sealed class NotYetException(string op) : Exception(op);

    /// <summary>What the compiler would refuse, in a call the runner has to build from a case at run time.</summary>
    private sealed class TypeRefusedException(string message) : Exception(message);
}
