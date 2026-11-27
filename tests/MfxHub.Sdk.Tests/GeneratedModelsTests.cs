using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MfxHub.Models;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// The models are generated from the public contract (tools/generate.ps1), public in
/// <c>MfxHub.Models</c> and .NET-named, and read and write the API's JSON as it is written.
/// </summary>
public sealed class GeneratedModelsTests
{
    private static readonly Type[] Models =
        [.. typeof(TenantTerminal).Assembly.GetExportedTypes().Where(t => t.Namespace == "MfxHub.Models")];

    /// <summary>The generator's rule for a name, so the test can tell a kept name from a missed one.</summary>
    private static string DotnetName(string name) =>
        string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p =>
            p.Length > 1 && p.All(char.IsAsciiLetterUpper)
                ? p[0] + p[1..].ToLowerInvariant()
                : char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>An error body from the API reads into the generated model, its details kept.</summary>
    [Fact]
    public void An_error_body_from_the_api_reads_into_the_generated_model_with_its_details()
    {
        const string body = """
            {"error": {"code": "upstream_error", "message": "the terminal saw no tick within 4 s",
                       "details": {"reason": "no_tick"}}}
            """;

        var error = JsonSerializer.Deserialize<Error>(body);

        Assert.NotNull(error);
        Assert.Equal("upstream_error", error.Error1.Code);
        Assert.Equal("the terminal saw no tick within 4 s", error.Error1.Message);
        var details = Assert.IsType<JsonElement>(error.Error1.Details);
        Assert.Equal("no_tick", details.GetProperty("reason").GetString());
    }

    /// <summary>
    /// Given the models generated from the contract / When their public names are read / Then every
    /// model is public in MfxHub.Models; every property and every enum member is .NET-named, with no
    /// underscore; each property keeps its wire name in [JsonPropertyName] and each member of an enum
    /// written as text keeps its wire name in [JsonStringEnumMemberName]; and a type keeps an
    /// underscore only where its .NET name is another type's, which the generator does not merge.
    /// </summary>
    [Fact]
    public void The_models_are_public_and_dotnet_named_and_keep_their_wire_names()
    {
        Assert.Contains(typeof(TenantTerminal), Models);
        Assert.True(Models.Length > 500, $"{Models.Length} public models");
        var names = Models.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var model in Models)
        {
            if (model.Name.Contains('_', StringComparison.Ordinal) && !names.Contains(DotnetName(model.Name)))
            {
                problems.Add($"{model.Name}: an underscore, and {DotnetName(model.Name)} is free");
            }
            if (model.IsEnum)
            {
                foreach (var member in model.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var wire = member.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
                    var text = member.GetCustomAttribute<System.Runtime.Serialization.EnumMemberAttribute>();
                    if (text is not null && wire?.Name != text.Value)
                    {
                        problems.Add($"{model.Name}.{member.Name}: wire name {wire?.Name ?? "missing"}, contract {text.Value}");
                    }
                    if (member.Name.TrimStart('_').Contains('_', StringComparison.Ordinal))
                    {
                        problems.Add($"{model.Name}.{member.Name}: an underscore");
                    }
                }
                continue;
            }
            foreach (var property in model.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.Name.Contains('_', StringComparison.Ordinal) || !char.IsUpper(property.Name[0]))
                {
                    problems.Add($"{model.Name}.{property.Name}: not .NET-named");
                }
                if (property.Name != "AdditionalProperties" && property.GetCustomAttribute<JsonPropertyNameAttribute>() is null)
                {
                    problems.Add($"{model.Name}.{property.Name}: no wire name");
                }
            }
        }
        Assert.Empty(problems);
    }

    /// <summary>
    /// Given a terminal as the API writes it / When it is read and written again / Then its fields have
    /// their .NET names in code (SameLoginTerminals, TenantTerminalState.LoggingIn) and their contract
    /// names on the wire (same_login_terminals, LOGGING_IN), and a field the model does not know is kept.
    /// </summary>
    [Fact]
    public void A_terminal_reads_with_dotnet_names_and_writes_the_contracts()
    {
        const string body = """
            {"id": "0191f0c2-aaaa-7bbb-8ccc-000000000001",
             "farm": {"id": "1c2d3e4f-5a6b-7c8d-9e0f-102030405060", "label": "test-farm"},
             "broker": {"id": "2c2d3e4f-5a6b-7c8d-9e0f-102030405060", "label": "Test Broker"},
             "server": {"id": "3c2d3e4f-5a6b-7c8d-9e0f-102030405060", "label": "TestBroker-Demo"},
             "server_kind": "demo", "same_login_terminals": 2, "streaming": false, "execution": true,
             "state": "LOGGING_IN", "instruments": ["EURUSD"], "created_at": "2026-10-23T09:00:00Z",
             "a_field_from_later": 7}
            """;

        var terminal = JsonSerializer.Deserialize<TenantTerminal>(body, MfxJson.Options)!;

        Assert.Equal(TenantTerminalState.LoggingIn, terminal.State);
        Assert.Equal(TenantTerminalServerKind.Demo, terminal.ServerKind);
        Assert.Equal(2, terminal.SameLoginTerminals);
        Assert.Equal("test-farm", terminal.Farm.Label);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero), terminal.CreatedAt);

        var written = JsonSerializer.SerializeToNode(terminal, MfxJson.Options)!.AsObject();
        Assert.Equal("LOGGING_IN", written["state"]!.GetValue<string>());
        Assert.Equal("demo", written["server_kind"]!.GetValue<string>());
        Assert.Equal(2, written["same_login_terminals"]!.GetValue<int>());
        Assert.Equal(7, written["a_field_from_later"]!.GetValue<int>());
        Assert.All(written.Select(p => p.Key), key => Assert.Equal(key.ToLowerInvariant(), key));
    }
}
