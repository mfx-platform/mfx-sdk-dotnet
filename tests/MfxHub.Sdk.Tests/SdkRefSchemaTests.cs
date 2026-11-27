using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MfxHub.SdkRef;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The contract this SDK vendors (spec/openapi.yaml), read as the suite's checker reads it: the keys of
/// <c>components.schemas</c>, four spaces in.
/// </summary>
internal static partial class Contract
{
    public static readonly Lazy<HashSet<string>> Keys = new(() =>
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var inSchemas = false;
        foreach (var line in File.ReadAllText(Path.Combine(SdkRefRun.Root, "spec", "openapi.yaml")).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line == "  schemas:")
            {
                inSchemas = true;
                continue;
            }
            if (inSchemas && OutOfSchemas().IsMatch(line))
            {
                inSchemas = false;
            }
            if (inSchemas && SchemaKey().Match(line) is { Success: true } key)
            {
                keys.Add(key.Groups[2].Value);
            }
        }
        return keys;
    });

    public static IReadOnlyList<Type> Models =>
        typeof(MfxClient).Assembly.GetExportedTypes().Where(t => t.Namespace == Generator.ModelsNamespace).ToList();

    /// <summary>The wire names a type's settable properties are read from (its own and inherited).</summary>
    public static HashSet<string> WireNames(Type model) =>
        model.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^ {0,3}\S")]
    private static partial Regex OutOfSchemas();

    [GeneratedRegex(@"^ {4}(['""]?)([A-Za-z0-9_.-]+)\1:(?:\s|$)")]
    private static partial Regex SchemaKey();
}

/// <summary>A generated model names the contract schema it was made from, and is listed with it.</summary>
public sealed class SdkRefModelSchemaTests
{
    /// <summary>
    /// Given the models tools/generate.ps1 writes from spec/openapi.yaml / When each is read for the schema it represents
    /// / Then a class or enum NSwag named after a key of components.schemas carries exactly that key (Position, the
    /// enum Venue, the cTrader grant objects), and a type NSwag made for an inline schema carries none.
    /// </summary>
    [Fact]
    public void A_generated_model_carries_the_schema_it_was_made_from()
    {
        Assert.Equal("Position", Generator.SchemaOf(typeof(MfxHub.Models.Position)));
        Assert.Equal("Venue", Generator.SchemaOf(typeof(MfxHub.Models.Venue)));
        Assert.Equal("CtraderGrant", Generator.SchemaOf(typeof(MfxHub.Models.CtraderGrant)));
        Assert.Equal("CtraderGrantStarted", Generator.SchemaOf(typeof(MfxHub.Models.CtraderGrantStarted)));

        var marked = 0;
        foreach (var model in Contract.Models)
        {
            var schema = Generator.SchemaOf(model);
            if (Contract.Keys.Value.Contains(model.Name))
            {
                Assert.True(model.Name == schema, $"{model.FullName} is the schema {model.Name} but carries {schema ?? "none"}");
                marked++;
            }
            else
            {
                Assert.True(schema is null, $"{model.FullName} is no schema of the contract but carries {schema}");
            }
        }
        Assert.True(marked > 300, $"only {marked} models carry a schema");
    }

    /// <summary>
    /// Given the SDK reference / When the generated models are read from it / Then each model a listed signature names is
    /// listed once, with its schema, the model doc and no members, on a page whose signatures name it (the first in page
    /// order: a position list and the cTrader grant on trading-accounts); an inline object's type is not listed.
    /// </summary>
    [Fact]
    public void A_model_a_signature_names_is_listed_with_its_schema_on_the_first_page_naming_it()
    {
        var pages = SdkRefRun.Json["pages"]!.AsArray().Select(p => p!.AsObject()).ToList();
        var models = pages.SelectMany(p => p["types"]!.AsArray().Select(t => (Page: (string)p["id"]!, Type: t!.AsObject())))
            .Where(e => SdkRefRun.IsModel(e.Type))
            .ToList();
        Assert.NotEmpty(models);
        Assert.Equal(models.Count, models.Select(m => (string)m.Type["name"]!).Distinct(StringComparer.Ordinal).Count());

        foreach (var (page, type) in models)
        {
            var name = (string)type["name"]!;
            var model = typeof(MfxClient).Assembly.GetType(Generator.ModelsNamespace + "." + name, throwOnError: true)!;
            Assert.Equal(Generator.SchemaOf(model), (string?)type["schema"]);
            Assert.Null(type["members"]);
            Assert.Equal(model.IsEnum ? "enum" : "class", (string?)type["kind"]);

            // Named as a type: before a member's name, or inside a generic, an array or a nullable.
            var asType = new Regex(@"(?<![\w.])" + name + @"(?=[?\[\]>,]| [A-Za-z_])");
            var own = pages.Single(p => (string)p["id"]! == page)["types"]!.AsArray().Select(t => t!.AsObject());
            Assert.True(own.Any(t => !SdkRefRun.IsModel(t) && Signatures(t).Any(asType.IsMatch)), $"no signature on {page} names {name}");
        }

        var positions = Assert.Single(models, m => (string?)m.Type["name"] == "PositionList");
        Assert.Equal("trading-accounts", positions.Page);
        Assert.Equal("PositionList", (string?)positions.Type["schema"]);
        Assert.Equal("public class PositionList", (string?)positions.Type["signature"]);
        Assert.Equal(Generator.ModelDoc("PositionList"), (string?)positions.Type["doc"]);
        Assert.Contains(models, m => (string?)m.Type["schema"] == "CtraderGrant" && m.Page == "trading-accounts");
        Assert.DoesNotContain(models, m => (string?)m.Type["name"] == "Account2");
    }

    private static IEnumerable<string> Signatures(JsonObject type) =>
        new[] { (string)type["signature"]! }.Concat((type["members"]?.AsArray() ?? []).Select(m => (string)m!["signature"]!));
}

/// <summary>Only a type that represents one contract object names a schema.</summary>
public sealed class SdkRefSchemaScopeTests
{
    /// <summary>
    /// Given the SDK reference / When every type and member is read for a schema / Then a client, an exception, a
    /// helper and a function name none, no member carries one, and the only types that do are the listed models and the
    /// hand-written types that represent one object (the stream events, the records read from one object).
    /// </summary>
    [Fact]
    public void A_client_an_exception_or_a_helper_names_no_schema()
    {
        foreach (var type in new[] { typeof(MfxClient), typeof(Execution), typeof(TradingAccounts), typeof(MfxApiException), typeof(Page<>), typeof(MfxJson), typeof(Secret), typeof(ExecutionResult) })
        {
            Assert.Null(Generator.SchemaOf(type));
        }
        foreach (var anchor in new[] { "client.MfxClient", "execution.Execution", "trading-accounts.TradingAccounts", "errors.MfxApiException", "client.Page", "execution.ExecutionResult" })
        {
            Assert.Null(SdkRefRun.Type(anchor)["schema"]);
        }

        Assert.DoesNotContain(SdkRefRun.Members, m => m.Member.ContainsKey("schema"));
        var withSchema = SdkRefRun.Types.Where(t => t.ContainsKey("schema")).ToList();
        Assert.DoesNotContain(withSchema, t => (string?)t["kind"] is "function" or "error" or "interface" or "alias" or "constant");

        var handWritten = typeof(MfxClient).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "MfxHub" && Generator.SchemaOf(t) is not null)
            .ToDictionary(t => CSharp.BareName(t), t => Generator.SchemaOf(t)!, StringComparer.Ordinal);
        Assert.Equal(
            handWritten.OrderBy(p => p.Key, StringComparer.Ordinal),
            withSchema.Where(t => !SdkRefRun.IsModel(t)).Select(t => KeyValuePair.Create((string)t["name"]!, (string)t["schema"]!)).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal("OrderPlaced", (string?)SdkRefRun.Type("stream.OrderPlaced")["schema"]);
        Assert.Equal("MdCandleClosed", (string?)SdkRefRun.Type("market-data.CandleClosed")["schema"]);
        Assert.Equal("BillingAction", (string?)SdkRefRun.Type("errors.PaymentAction")["schema"]);
    }
}

/// <summary>Every schema the reference names is one the contract has.</summary>
public sealed class SdkRefSchemaContractTests
{
    /// <summary>
    /// Given the generated and the committed SDK reference and the vendored contract / When every type's schema is
    /// looked up / Then each is a key of components.schemas, case kept, in the format's pattern; the same holds for
    /// every ContractSchema the SDK declares, and each hand-written type that names one carries only fields of that
    /// object (an event's kind, a message's channel and seq aside).
    /// </summary>
    [Fact]
    public void Every_named_schema_exists_in_the_contract()
    {
        var keys = Contract.Keys.Value;
        Assert.Contains("CtraderAccountsChoice", keys);
        var pattern = new Regex("^[A-Za-z][A-Za-z0-9]*$");

        var committed = JsonNode.Parse(File.ReadAllText(Path.Combine(SdkRefRun.Root, "docs", "sdkref.json")))!;
        var named = SdkRefRun.Types.Concat(committed["pages"]!.AsArray().SelectMany(p => p!["types"]!.AsArray()).Select(t => t!.AsObject()))
            .Where(t => t.ContainsKey("schema"))
            .Select(t => (string)t["schema"]!)
            .ToList();
        Assert.NotEmpty(named);
        foreach (var schema in named)
        {
            Assert.Matches(pattern, schema);
            Assert.True(keys.Contains(schema), $"the contract has no schema {schema}");
        }

        var declared = typeof(MfxClient).Assembly.GetTypes().Where(t => Generator.SchemaOf(t) is not null).ToList();
        foreach (var type in declared)
        {
            Assert.True(keys.Contains(Generator.SchemaOf(type)!), $"{type.FullName} names {Generator.SchemaOf(type)}, which the contract lacks");
        }

        // A hand-written type wraps exactly one object: what it reads is that object's fields.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal) { ["from_state"] = "from", ["to_state"] = "to", ["coverage"] = "kind" };
        var envelope = new HashSet<string>(StringComparer.Ordinal) { "kind", "channel", "seq" };
        foreach (var type in declared.Where(t => t.Namespace == "MfxHub"))
        {
            var schema = Generator.SchemaOf(type)!;
            var model = Contract.Models.Single(m => Generator.SchemaOf(m) == schema);
            var wire = Contract.WireNames(model);
            var read = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is not null)
                .Select(p => Snake(p.Name))
                .Where(n => !envelope.Contains(n))
                .Select(n => renamed.GetValueOrDefault(n, n))
                .ToList();
            Assert.NotEmpty(read);
            foreach (var field in read)
            {
                Assert.True(wire.Contains(field), $"{type.Name} reads {field}, which {schema} does not have");
            }
        }
    }

    private static string Snake(string name) => Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
}
