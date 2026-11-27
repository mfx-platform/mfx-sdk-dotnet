using System.Reflection;
using System.Text.Json.Nodes;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The surface check: the shared surface
/// (spec/surface.json), looked for in this SDK's public API through the idiom map
/// (tools/surface-check/map.json). An entry the map holds as pending is skipped, and named with the
/// pull request that brings it.
/// </summary>
internal static class Surface
{
    public static readonly JsonObject Reference = Load("surface.json");
    public static readonly JsonObject Map = Load("surface-map.json");
    private static readonly Assembly Sdk = typeof(MfxClient).Assembly;

    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)))!.AsObject();

    /// <summary>
    /// An error name in spec/surface.json as this SDK's type name: <c>FooError</c> is <c>FooException</c>, and a name
    /// without the suffix (<c>TermsNotAccepted</c>) gains it (<c>TermsNotAcceptedException</c>).
    /// </summary>
    public static string ErrorName(string python) =>
        (python.EndsWith("Error", StringComparison.Ordinal) ? python[..^"Error".Length] : python) + "Exception";

    public static string Pascal(string snake) =>
        string.Concat(snake.Split('_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    private static string Camel(string snake)
    {
        var pascal = Pascal(snake);
        return pascal.Length > 0 ? char.ToLowerInvariant(pascal[0]) + pascal[1..] : pascal;
    }

    private static string? Mapped(string section, string key) => Map[section]?[key]?.GetValue<string>();

    private static JsonObject Pending => Map["pending"]!.AsObject();

    /// <summary>
    /// Whether the map holds an entry as pending: by its key (<c>terminals.create</c>,
    /// <c>errors.PaymentRequiredError</c>, <c>events.CandleClosed</c>) or by its whole resource (<c>execution</c>).
    /// </summary>
    public static bool IsPending(string key) =>
        Pending.ContainsKey(key) || (key.IndexOf('.', StringComparison.Ordinal) is var dot and > 0 && Pending.ContainsKey(key[..dot]));

    /// <summary>Every key the reference has: its methods, its resources, its errors and its events.</summary>
    public static HashSet<string> Keys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Reference["methods"]!.AsArray())
        {
            keys.Add(e!["resource"]!.GetValue<string>());
            keys.Add($"{e["resource"]}.{(e["method"] ?? e["property"] ?? e["protocol"])}");
        }
        foreach (var e in Reference["errors"]!.AsArray())
        {
            keys.Add("errors." + e!["error"]!.GetValue<string>());
        }
        foreach (var e in Reference["events"]!.AsArray())
        {
            keys.Add("events." + e!["event"]!.GetValue<string>());
        }
        return keys;
    }

    /// <summary>A call the reference makes async: a task, or an async iterator.</summary>
    private static bool IsAsync(Type returns) =>
        typeof(Task).IsAssignableFrom(returns)
        || (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));

    /// <summary>The public type a resource is, by the map or by the rule (<c>trading_accounts</c> is <c>MfxHub.TradingAccounts</c>).</summary>
    private static Type? TypeOf(string resource) => Sdk.GetType(Mapped("resources", resource) ?? "MfxHub." + Pascal(resource));

    /// <summary>One problem for each entry of <paramref name="phase"/> this SDK's public surface does not have.</summary>
    public static List<string> Missing(string phase)
    {
        var problems = new List<string>();
        var resources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in Reference["methods"]!.AsArray().Select(e => e!.AsObject()))
        {
            if (entry["phase"]!.GetValue<string>() != phase)
            {
                continue;
            }
            var resource = entry["resource"]!.GetValue<string>();
            var name = (entry["method"] ?? entry["property"] ?? entry["protocol"])!.GetValue<string>();
            var key = $"{resource}.{name}";
            if (IsPending(key))
            {
                continue;
            }
            resources.Add(resource);
            var type = TypeOf(resource);
            if (type is null || !type.IsPublic)
            {
                problems.Add($"{key}: no public type for '{resource}'");
                continue;
            }
            if (entry["protocol"] is not null)
            {
                var wanted = Mapped("protocols", key);
                if (wanted is null || type.GetInterfaces().All(i => (i.IsGenericType ? i.GetGenericTypeDefinition() : i).FullName != wanted))
                {
                    problems.Add($"{key}: {type.Name} does not implement {wanted ?? "a mapped interface"}");
                }
                continue;
            }
            if (entry["property"] is not null)
            {
                if (type.GetProperty(Pascal(name), BindingFlags.Public | BindingFlags.Instance) is null)
                {
                    problems.Add($"{key}: no public property {type.Name}.{Pascal(name)}");
                }
                continue;
            }
            var async = entry["async"]?.GetValue<bool>() == true;
            var member = Mapped("methods", key) ?? Pascal(name) + (async ? "Async" : string.Empty);
            MethodBase[] candidates = member == ".ctor"
                ? type.GetConstructors()
                : type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == member).ToArray();
            if (candidates.Length == 0)
            {
                problems.Add($"{key}: no public {type.Name}.{member}");
                continue;
            }
            foreach (var arg in entry["args"]?.AsArray().Select(a => a!.AsObject()) ?? [])
            {
                var argName = arg["name"]!.GetValue<string>();
                var target = Map["arguments"]?[key]?[argName]?.GetValue<string>() ?? Camel(argName);
                if (target.Contains('.', StringComparison.Ordinal))
                {
                    var (optionsName, property) = (target[..target.IndexOf('.', StringComparison.Ordinal)], target[(target.IndexOf('.', StringComparison.Ordinal) + 1)..]);
                    var options = Sdk.GetType($"MfxHub.{optionsName}");
                    if (options?.GetProperty(property) is null
                        || !candidates.Any(c => c.GetParameters().Any(p => p.ParameterType == options)))
                    {
                        problems.Add($"{key}({argName}): no {target} on a parameter of {type.Name}.{member}");
                    }
                }
                else if (!candidates.Any(c => c.GetParameters().Any(p => p.Name == target))
                    // Seconds as a number (timeout_s) are a TimeSpan in .NET (timeout).
                    && !(argName.EndsWith("_s", StringComparison.Ordinal) && candidates.Any(c => c.GetParameters().Any(
                        p => p.Name == Camel(argName[..^2]) && Nullable.GetUnderlyingType(p.ParameterType) is var t && (t ?? p.ParameterType) == typeof(TimeSpan)))))
                {
                    problems.Add($"{key}({argName}): {type.Name}.{member} has no parameter {target}");
                }
            }
            if ((async || member.EndsWith("Async", StringComparison.Ordinal)) && member != "Dispose"
                && !candidates.OfType<MethodInfo>().All(m => IsAsync(m.ReturnType)))
            {
                problems.Add($"{key}: {type.Name}.{member} is async in the reference but returns no Task or IAsyncEnumerable");
            }
            if (phase == "enriched" && candidates.OfType<MethodInfo>().Any(m => IsAsync(m.ReturnType)
                && m.GetParameters() is var ps && (ps.Length == 0 || ps[^1].ParameterType != typeof(CancellationToken))))
            {
                problems.Add($"{key}: {type.Name}.{member} does not take a CancellationToken last");
            }
        }
        // Every domain resource is reached from the client, as client.<resource> is in the reference.
        foreach (var resource in resources.Where(r => r is not "client" and not "stream"))
        {
            var property = typeof(MfxClient).GetProperty(Pascal(resource), BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.PropertyType != TypeOf(resource))
            {
                problems.Add($"{resource}: no public MfxClient.{Pascal(resource)} of type {TypeOf(resource)?.Name ?? "?"}");
            }
        }
        foreach (var error in Reference["errors"]!.AsArray().Where(e => e!["phase"]!.GetValue<string>() == phase)
            .Select(e => e!["error"]!.GetValue<string>()).Distinct())
        {
            var dotnet = "MfxHub." + ErrorName(error);
            if (!IsPending("errors." + error) && Sdk.GetType(dotnet) is not { IsPublic: true })
            {
                problems.Add($"errors.{error}: no public {dotnet}");
            }
        }
        foreach (var e in Reference["events"]!.AsArray().Select(e => e!.AsObject()).Where(e => e["phase"]!.GetValue<string>() == phase))
        {
            var name = e["event"]!.GetValue<string>();
            if (IsPending("events." + name))
            {
                continue;
            }
            var type = Sdk.GetType(Mapped("events", name) ?? "MfxHub." + name);
            if (type is not { IsPublic: true })
            {
                problems.Add($"events.{name}: no public type MfxHub.{name}");
                continue;
            }
            foreach (var field in e["fields"]!.AsArray().Select(f => f!.GetValue<string>()))
            {
                if (type.GetProperty(Pascal(field.TrimEnd('_')), BindingFlags.Public | BindingFlags.Instance) is null)
                {
                    problems.Add($"events.{name}.{field}: no public property {type.Name}.{Pascal(field.TrimEnd('_'))}");
                }
            }
        }
        return problems;
    }
}

/// <summary>The surface check for Core: every Core entry of the reference surface is in the public API.</summary>
public sealed class SurfaceTests
{
    /// <summary>
    /// Given the shared surface.json and this SDK's idiom map / When every Core entry is
    /// looked for in the public API / Then each method, property, protocol, argument and error is
    /// there under its .NET name, async where the reference is; an entry the map holds as pending is
    /// named with the step that brings it, and the map names only entries the reference has.
    /// </summary>
    [Fact]
    public void Every_core_entry_of_the_reference_surface_is_in_the_public_api()
    {
        Assert.Empty(Surface.Missing("core"));

        var keys = Surface.Keys();
        foreach (var section in new[] { "protocols", "methods", "arguments", "pending", "events" })
        {
            Assert.All(Surface.Map[section]?.AsObject() ?? [], entry => Assert.Contains(entry.Key, keys));
        }
        Assert.All(Surface.Map["pending"]!.AsObject(), entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value!.GetValue<string>())));
    }
}

/// <summary>The surface check for Enriched: the domain clients, their events and their errors.</summary>
public sealed class EnrichedSurfaceTests
{
    /// <summary>
    /// Given the shared surface.json and this SDK's idiom map / When every Enriched entry that
    /// the map does not hold as pending is looked for in the public API / Then each resource is a
    /// public property of the client, each method is there under its .NET name with every argument,
    /// async and taking a CancellationToken last, and each event is a public type with every field.
    /// </summary>
    [Fact]
    public void Every_enriched_entry_that_has_landed_is_in_the_public_api()
    {
        Assert.Empty(Surface.Missing("enriched"));
    }

    /// <summary>
    /// Given the idiom map's pending section / When it is read against the reference / Then every
    /// pending key is a resource, method, error or event the reference has, and each names the pull
    /// request that brings it.
    /// </summary>
    [Fact]
    public void Every_pending_entry_is_the_references_and_names_what_brings_it()
    {
        var keys = Surface.Keys();
        Assert.All(Surface.Map["pending"]!.AsObject(), entry =>
        {
            Assert.Contains(entry.Key, keys);
            Assert.Contains("AB#", entry.Value!.GetValue<string>(), StringComparison.Ordinal);
        });
    }
}
