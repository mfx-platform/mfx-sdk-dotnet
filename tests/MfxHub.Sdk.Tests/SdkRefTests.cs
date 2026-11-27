using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MfxHub.SdkRef;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The SDK reference, docs/sdkref.json (format mfx-sdkref/1): generated once per test run from the built SDK, its
/// XML documentation and the vendored contract, and read back as JSON.
/// </summary>
internal static class SdkRefRun
{
    private static readonly Lazy<(byte[] Bytes, JsonObject Json)> Generated = new(() =>
    {
        var bytes = Generator.Build(File.ReadAllText(Path.Combine(Root, "spec", "openapi.yaml"))).ToJson();
        return (bytes, JsonNode.Parse(bytes)!.AsObject());
    });

    public static string Root => Repository.Root(AppContext.BaseDirectory);

    public static byte[] Bytes => Generated.Value.Bytes;

    public static JsonObject Json => Generated.Value.Json;

    public static IEnumerable<JsonObject> Types =>
        Json["pages"]!.AsArray().SelectMany(p => p!["types"]!.AsArray()).Select(t => t!.AsObject());

    public static IEnumerable<(JsonObject Type, JsonObject Member)> Members =>
        Types.SelectMany(t => (t["members"]?.AsArray() ?? []).Select(m => (t, m!.AsObject())));

    /// <summary>Whether a listed type is a generated model (<c>MfxHub.Models</c>), which carries the model doc.</summary>
    public static bool IsModel(JsonObject type) =>
        type["schema"] is { } schema && (string?)type["doc"] == Generator.ModelDoc((string)schema!);

    public static JsonObject Type(string anchor) => Types.Single(t => (string?)t["anchor"] == anchor);

    public static JsonObject Member(string anchor) => Members.Single(m => (string?)m.Member["anchor"] == anchor).Member;

    /// <summary>Every Markdown value in the file (intro, doc, deprecated), with where it is.</summary>
    public static IEnumerable<(string Where, string Text)> Markdown()
    {
        var found = new List<(string, string)>();
        Walk(Json, "$", found);
        return found;

        static void Walk(JsonNode? node, string where, List<(string, string)> found)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key is "intro" or "doc" or "deprecated" && value is JsonValue)
                        {
                            found.Add((where + "." + key, (string)value!));
                        }
                        else
                        {
                            Walk(value, where + "." + key, found);
                        }
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        Walk(a[i], where + "[" + i + "]", found);
                    }
                    break;
            }
        }
    }

    /// <summary>Markdown with fenced blocks removed and each code span read as <c>code</c>, as the suite's checker reads it.</summary>
    public static string WithoutCode(string markdown) =>
        Regex.Replace(
            Regex.Replace(markdown, @"^ {0,3}(`{3,}|~{3,}).*?^ {0,3}\1[ \t]*$", string.Empty, RegexOptions.Multiline | RegexOptions.Singleline),
            @"(`+)(?!`).*?(?<!`)\1(?!`)", "code", RegexOptions.Singleline);
}

/// <summary>The file's shape: pages, types, members, anchors and signatures.</summary>
public sealed class SdkRefShapeTests
{
    private static readonly Regex TypeAnchor = new(@"^[a-z]+(-[a-z]+)*\.[A-Za-z_][A-Za-z0-9_]*$");
    private static readonly Regex MemberAnchor = new(@"^[a-z]+(-[a-z]+)*\.[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>
    /// Given the generator run over MfxHub.Sdk / When the document is built / Then it is format mfx-sdkref/1 for
    /// csharp and package MfxHub.Sdk at the SDK's version, its pages are in the format's fixed order, and every
    /// public type of the MfxHub namespace is on exactly one page, every exception on errors.
    /// </summary>
    [Fact]
    public void Every_public_type_is_on_one_page_in_the_fixed_order()
    {
        var json = SdkRefRun.Json;
        Assert.Equal("mfx-sdkref/1", (string?)json["format"]);
        Assert.Equal("csharp", (string?)json["language"]);
        Assert.Equal("MfxHub.Sdk", (string?)json["package"]);
        Assert.Equal(MfxClient.UserAgent.Split('/')[1], (string?)json["version"]);
        Assert.Equal("dotnet add package MfxHub.Sdk", (string?)json["install"]);

        var pages = json["pages"]!.AsArray().Select(p => (string)p!["id"]!).ToList();
        Assert.Equal(Generator.PageOrder, pages);

        // The generated models a signature names are listed too, and proved by SdkRefModelSchemaTests.
        var listed = SdkRefRun.Types.Where(t => !SdkRefRun.IsModel(t)).Select(t => (string)t["anchor"]!).ToList();
        var exported = typeof(MfxClient).Assembly.GetExportedTypes().Where(t => t.Namespace == "MfxHub").ToList();
        Assert.Equal(exported.Count, listed.Count);
        foreach (var type in exported)
        {
            var anchor = Assert.Single(listed, a => a.EndsWith("." + CSharp.BareName(type), StringComparison.Ordinal));
            if (typeof(Exception).IsAssignableFrom(type))
            {
                Assert.StartsWith("errors.", anchor, StringComparison.Ordinal);
            }
        }
        Assert.DoesNotContain(listed, a => a.Contains("Models", StringComparison.Ordinal));
    }

    /// <summary>
    /// Given the document / When every anchor is read / Then each is unique, starts with its page id (a member's with
    /// its type's), and has the format's shape; overloads share one anchor, shown by the overload with the most
    /// parameters, with every overload's signature listed in the doc.
    /// </summary>
    [Fact]
    public void Every_anchor_is_unique_and_starts_with_its_page_and_type()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in SdkRefRun.Json["pages"]!.AsArray())
        {
            var id = (string)page!["id"]!;
            foreach (var type in page["types"]!.AsArray())
            {
                var anchor = (string)type!["anchor"]!;
                Assert.Matches(TypeAnchor, anchor);
                Assert.StartsWith(id + ".", anchor, StringComparison.Ordinal);
                Assert.True(seen.Add(anchor), anchor);
                foreach (var member in type["members"]?.AsArray() ?? [])
                {
                    var memberAnchor = (string)member!["anchor"]!;
                    Assert.Matches(MemberAnchor, memberAnchor);
                    Assert.StartsWith(anchor + ".", memberAnchor, StringComparison.Ordinal);
                    Assert.True(seen.Add(memberAnchor), memberAnchor);
                }
            }
        }

        var constructor = SdkRefRun.Member("errors.MfxTransportException.MfxTransportException");
        Assert.Equal("public MfxTransportException(string message, bool mayHaveReached, Exception? inner = null)", (string?)constructor["signature"]);
        Assert.Contains("- `public MfxTransportException()`", (string)constructor["doc"]!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Given the document / When every signature is read / Then each is one complete line as C# writes it, with
    /// nullability and defaults, never elided; and no optional key is null or empty.
    /// </summary>
    [Fact]
    public void Every_signature_is_one_complete_line_with_its_nullability()
    {
        foreach (var signature in SdkRefRun.Types.Select(t => (string)t["signature"]!)
            .Concat(SdkRefRun.Members.Select(m => (string)m.Member["signature"]!)))
        {
            Assert.DoesNotContain('\n', signature);
            Assert.DoesNotContain("...", signature, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, signature);
        }

        Assert.Equal(
            "public Task<ExecutionResult> PlaceOrderAsync(Guid terminalId, string symbol, Side side, decimal volume, string idempotencyKey, "
            + "OrderType orderType = OrderType.Market, decimal? price = null, decimal? stopLimitPrice = null, decimal? sl = null, "
            + "decimal? tp = null, int? deviationPoints = null, DateTimeOffset? expiresAt = null, int? magic = null, string? comment = null, "
            + "CancellationToken cancellationToken = default)",
            (string?)SdkRefRun.Member("execution.Execution.PlaceOrderAsync")["signature"]);
        Assert.Equal(
            "public sealed record Tickets(long? Order = null, long? Deal = null, long? Position = null)",
            (string?)SdkRefRun.Type("execution.Tickets")["signature"]);
        Assert.Equal("public sealed class Page<T> : IReadOnlyList<T>", (string?)SdkRefRun.Type("client.Page")["signature"]);
        Assert.Equal("public T this[int index] { get; }", (string?)SdkRefRun.Member("client.Page.Item")["signature"]);
        Assert.Equal("public T? Value { get; }", (string?)SdkRefRun.Member("copy.Change.Value")["signature"]);
        Assert.Equal("public static Change<T> To(T value)", (string?)SdkRefRun.Member("copy.Change.To")["signature"]);
        Assert.Equal("public const int DefaultReadRetries = 3",(string?)SdkRefRun.Member("client.MfxClient.DefaultReadRetries")["signature"]);

        AssertNoEmptyOptional(SdkRefRun.Json);
    }

    private static void AssertNoEmptyOptional(JsonNode? node)
    {
        string[] optional = ["extends", "code", "members", "params", "default", "returns", "raises", "operation", "deprecated", "links"];
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    Assert.NotNull(value);
                    if (optional.Contains(key))
                    {
                        Assert.False(value is JsonArray { Count: 0 } or JsonObject { Count: 0 }, key + " is empty");
                        Assert.False(value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0, key + " is empty");
                    }
                    AssertNoEmptyOptional(value);
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    AssertNoEmptyOptional(item);
                }
                break;
        }
    }
}

/// <summary>Doc comments as Markdown: cross-references, code spans and paragraphs, and no HTML.</summary>
public sealed class SdkRefMarkdownTests
{
    private static string Convert(string xml)
    {
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal)
        {
            ["T:MfxHub.Execution"] = new("execution.Execution", "Execution", typeof(Execution)),
            ["M:MfxHub.Execution.GetCommandAsync(System.Guid,System.String,System.Threading.CancellationToken)"] =
                new("execution.Execution.GetCommandAsync", "Execution.GetCommandAsync", typeof(Execution)),
        };
        return new Markdown(cref => targets.GetValueOrDefault(cref)).Convert(XElement.Parse(xml, LoadOptions.PreserveWhitespace), typeof(Terminals));
    }

    /// <summary>
    /// Given XML doc comments / When they are converted / Then a see cref to something in the file is [Name](#anchor),
    /// one to anything else a code span, paramref and c are code spans, para makes paragraphs, a generic or anything
    /// with an angle bracket is in a code span, and whitespace collapses.
    /// </summary>
    [Fact]
    public void Doc_comments_become_markdown_with_links_and_code_spans()
    {
        Assert.Equal(
            "Returns an [Execution](#execution.Execution) via [Execution.GetCommandAsync](#execution.Execution.GetCommandAsync).",
            Convert("<summary>Returns an <see cref=\"T:MfxHub.Execution\"/>  via\n                "
                + "<see cref=\"M:MfxHub.Execution.GetCommandAsync(System.Guid,System.String,System.Threading.CancellationToken)\"/>.</summary>"));
        Assert.Equal(
            "A `TenantTerminal`, a `Page.NextCursor`, an `ArgumentException`, `terminalId` and `T`.",
            Convert("""<summary>A <see cref="T:MfxHub.Models.TenantTerminal"/>, a <see cref="P:MfxHub.Page`1.NextCursor"/>, an <see cref="T:System.ArgumentException"/>, <paramref name="terminalId"/> and <typeparamref name="T"/>.</summary>"""));
        Assert.Equal(
            "One.\n\nTwo with `code`.",
            Convert("<remarks>One.<para>Two with <c>code</c>.</para></remarks>"));
        Assert.Equal(
            "A `Page<T>` of `service_agreement_<plan>`.",
            Convert("<summary>A Page&lt;T&gt; of <c>service_agreement_&lt;plan&gt;</c>.</summary>"));
    }

    /// <summary>
    /// Given the generated document / When every Markdown value is read as the suite's checker reads it / Then every
    /// [Name](#anchor) names an anchor in the file, every other link is https, and no angle bracket is left outside code.
    /// </summary>
    [Fact]
    public void Every_cross_reference_resolves_and_no_html_is_left()
    {
        var anchors = SdkRefRun.Types.Select(t => (string)t["anchor"]!)
            .Concat(SdkRefRun.Members.Select(m => (string)m.Member["anchor"]!)).ToHashSet(StringComparer.Ordinal);
        var links = 0;
        foreach (var (where, text) in SdkRefRun.Markdown())
        {
            var plain = SdkRefRun.WithoutCode(text);
            Assert.False(plain.Contains('<', StringComparison.Ordinal) || plain.Contains('>', StringComparison.Ordinal), where + ": " + text);
            foreach (Match link in Regex.Matches(plain, @"\[([^\[\]]*)\]\(([^()]*)\)"))
            {
                var target = link.Groups[2].Value;
                if (target.StartsWith('#'))
                {
                    links++;
                    Assert.True(anchors.Contains(target[1..]), $"{where}: {link.Value} names no anchor");
                }
                else
                {
                    Assert.StartsWith("https://", target, StringComparison.Ordinal);
                }
            }
        }
        Assert.True(links > 100, "the doc comments' see crefs became links");
    }
}

/// <summary>Which contract operation each member calls, and which code each error carries.</summary>
public sealed class SdkRefOperationTests
{
    private static readonly IReadOnlyList<ContractOperation> Contract =
        Operations.ReadContract(File.ReadAllText(Path.Combine(SdkRefRun.Root, "spec", "openapi.yaml")));

    /// <summary>
    /// Given the vendored contract / When the document's operations are read / Then each is an operationId of the
    /// contract, every async method of a domain client (a type the client exposes) names the one it calls, and the
    /// known ones are right: placeOrder, closePosition, getTerminal, openStream.
    /// </summary>
    [Fact]
    public void Every_domain_method_names_the_contract_operation_it_calls()
    {
        var ids = Contract.Select(o => o.OperationId).ToHashSet(StringComparer.Ordinal);
        Assert.True(ids.Count > 100);
        foreach (var (_, member) in SdkRefRun.Members.Where(m => m.Member["operation"] is not null))
        {
            Assert.Contains((string)member["operation"]!, ids);
        }

        var resources = typeof(MfxClient).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType)
            .Where(t => t.Assembly == typeof(MfxClient).Assembly && t.GetConstructors().Length == 0)
            .ToList();
        Assert.Contains(typeof(Execution), resources);
        var missing = SdkRefRun.Members
            .Where(m => resources.Any(r => "." + CSharp.BareName(r) == "." + ((string)m.Type["anchor"]!).Split('.')[1]))
            .Where(m => (string?)m.Member["kind"] == "method" && !((string)m.Member["signature"]!).Contains(" static ", StringComparison.Ordinal))
            .Where(m => m.Member["operation"] is null)
            .Select(m => (string)m.Member["anchor"]!)
            .ToList();
        Assert.Empty(missing);

        Assert.Equal("placeOrder", (string?)SdkRefRun.Member("execution.Execution.PlaceOrderAsync")["operation"]);
        Assert.Equal("closePosition", (string?)SdkRefRun.Member("execution.Execution.ClosePositionAsync")["operation"]);
        Assert.Equal("getTerminal", (string?)SdkRefRun.Member("terminals.Terminals.GetAsync")["operation"]);
        Assert.Equal("openStream", (string?)SdkRefRun.Member("client.MfxClient.StreamAsync")["operation"]);
    }

    /// <summary>
    /// Given the contract's paths / When a request is matched / Then the template with the most literal segments wins,
    /// so /terminals/{id}/orders/pending is listPendingOrders and not a template that takes any segment.
    /// </summary>
    [Fact]
    public void A_request_matches_the_closest_path_of_the_contract()
    {
        Assert.Equal("listPendingOrders", Operations.Match(Contract, "GET", "/terminals/0191f0c2/orders/pending")?.OperationId);
        Assert.Equal("placeOrder", Operations.Match(Contract, "POST", "/terminals/0191f0c2/orders")?.OperationId);
        Assert.Null(Operations.Match(Contract, "GET", "/nowhere"));
    }

    /// <summary>
    /// Given the SDK's own error mapping / When the errors page is read / Then every exception is kind error, each
    /// type the platform's code maps to carries that code, and a base class with no single code carries none.
    /// </summary>
    [Fact]
    public void Every_error_carries_the_code_it_maps()
    {
        var codes = Generator.ErrorCodes(typeof(MfxClient).Assembly);
        foreach (var (type, code) in codes)
        {
            var entry = SdkRefRun.Type("errors." + type.Name);
            Assert.Equal("error", (string?)entry["kind"]);
            Assert.Equal(code, (string?)entry["code"]);
        }
        Assert.Equal("service_agreement_not_accepted", (string?)SdkRefRun.Type("errors.ServiceAgreementNotAcceptedException")["code"]);
        Assert.Null(SdkRefRun.Type("errors.MfxApiException")["code"]);
        Assert.Null(SdkRefRun.Type("errors.EdgeException")["code"]);
        Assert.Equal(["ConflictException"], SdkRefRun.Type("errors.StateException")["extends"]!.AsArray().Select(e => (string)e!));
    }
}

/// <summary>The committed file: current, deterministic and free of anything internal.</summary>
public sealed class SdkRefPublishedTests
{
    /// <summary>
    /// Given the committed docs/sdkref.json / When the generator runs again, twice / Then both runs write the same
    /// bytes and the committed file is exactly those bytes (no drift): UTF-8 without a byte order mark, LF only and
    /// one LF at the end.
    /// </summary>
    [Fact]
    public void The_committed_reference_is_what_the_generator_writes()
    {
        var again = Generator.Build(File.ReadAllText(Path.Combine(SdkRefRun.Root, "spec", "openapi.yaml"))).ToJson();
        Assert.Equal(SdkRefRun.Bytes, again);

        var committed = File.ReadAllBytes(Path.Combine(SdkRefRun.Root, "docs", "sdkref.json"));
        Assert.False(committed.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.DoesNotContain((byte)'\r', committed);
        Assert.Equal((byte)'\n', committed[^1]);
        Assert.NotEqual((byte)'\n', committed[^2]);
        Assert.True(SdkRefRun.Bytes.AsSpan().SequenceEqual(committed),
            "docs/sdkref.json is not what the generator writes: run `dotnet run --project tools/SdkRef -c Release` and commit it");
    }

    /// <summary>
    /// Given the generated and the committed reference / When each is searched for internal words / Then neither holds
    /// a work item or decision id, MT5 as prose writes it, cell or cells, or an internal repository's name; the
    /// terminal is what the doc comments say, and a word written into a doc comment is caught.
    /// </summary>
    [Fact]
    public void No_internal_word_reaches_the_reference()
    {
        Assert.DoesNotMatch(Generator.Internal(), Encoding.UTF8.GetString(SdkRefRun.Bytes));
        Assert.DoesNotMatch(Generator.Internal(), File.ReadAllText(Path.Combine(SdkRefRun.Root, "docs", "sdkref.json")));
        foreach (var leak in new[] { "see AB" + "#123", "DEC" + "-7 says", "an MT5 login", "its cell", "the cells", "mfx-sdk-dotnet-intern" + "al" })
        {
            Assert.Matches(Generator.Internal(), leak);
        }
        foreach (var fine in new[] { "Mt5Retcode", "mt5_licence", "CancellationToken", "cancelled" })
        {
            Assert.DoesNotMatch(Generator.Internal(), fine);
        }
    }
}

/// <summary>The suite's canonical JSON form, and the generator's own checks.</summary>
public sealed class SdkRefCanonicalJsonTests
{
    /// <summary>
    /// Given strings with quotes, backslashes, control characters, non-ASCII and an emoji / When written / Then the
    /// text is exactly what Python's json.dumps(indent=2, ensure_ascii=False) writes, then one LF: two-space indent,
    /// a colon and a space after a key, short escapes where JSON has them, lower-case \u00xx otherwise, and
    /// everything else as itself.
    /// </summary>
    [Fact]
    public void The_file_is_written_in_the_canonical_form()
    {
        var value = new OrderedJson()
            .With("a", "q\"b\\n\n\t\u0001\u001f é €\U0001F600 <x>")
            .With("list", new List<object> { "x", new OrderedJson().With("k", "v") })
            .With("empty", new OrderedJson())
            .With("none", null);

        Assert.Equal(
            "{\n  \"a\": \"q\\\"b\\\\n\\n\\t\\u0001\\u001f é €\U0001F600 <x>\",\n  \"list\": [\n    \"x\",\n    {\n      \"k\": \"v\"\n    }\n  ],\n  \"empty\": {}\n}\n",
            CanonicalJson.Write(value));
    }

    /// <summary>
    /// Given the generator's page list / When it is read against the SDK / Then every type it places is a public type
    /// of the SDK (a public type it does not place stops the generator), and each of the twelve pages has an intro.
    /// </summary>
    [Fact]
    public void Every_listed_type_exists_and_every_page_has_an_intro()
    {
        var exported = typeof(MfxClient).Assembly.GetExportedTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(Generator.PageOf.Keys, name => Assert.Contains(name, exported));
        Assert.All(Generator.PageOrder, id => Assert.False(string.IsNullOrWhiteSpace(Generator.Intros[id])));
        Assert.Equal(12, Generator.PageOrder.Count);
    }
}
