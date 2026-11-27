using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MfxHub.Sdk.Conformance;

/// <summary>
/// The shared suite's fake MFX Hub, started once for the run. It is the suite's own Python program,
/// vendored in ./suite; it listens on a free localhost port and prints
/// <c>listening http://127.0.0.1:&lt;port&gt;/v1</c> when it is ready.
/// </summary>
/// <remarks>
/// The interpreter is <c>MFX_SUITE_PYTHON</c> when set (CI points it at an environment installed
/// from hub-requirements.txt), otherwise <c>python3</c>, or <c>python</c> on Windows.
/// </remarks>
public sealed class Hub : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(20);

    private Process? _process;
    private readonly HttpClient _control = new();

    /// <summary>The vendored suite's directory.</summary>
    public static string Suite { get; } = Path.Combine(AppContext.BaseDirectory, "suite");

    /// <summary>The API base the hub serves, as a client is built with it.</summary>
    public Uri BaseUrl { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        var python = Environment.GetEnvironmentVariable("MFX_SUITE_PYTHON")
            ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        var start = new ProcessStartInfo(python)
        {
            ArgumentList = { "-m", "mfx_fake_hub", "--cases", Path.Combine(Suite, "cases") },
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment = { ["PYTHONPATH"] = Suite, ["PYTHONDONTWRITEBYTECODE"] = "1" },
        };
        _process = Process.Start(start) ?? throw new InvalidOperationException("the fake hub could not be started");
        using var timeout = new CancellationTokenSource(Startup);
        var line = await _process.StandardOutput.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("the fake hub stopped before listening");
        if (!line.StartsWith("listening ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"the fake hub said '{line}', not where it listens");
        }
        BaseUrl = new Uri(line["listening ".Length..].Trim());
        _control.BaseAddress = new Uri(BaseUrl.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>Loads a case; the hub answers it with its fixtures expanded.</summary>
    public async Task<JsonObject> LoadAsync(string id)
    {
        using var answer = await _control.PostAsJsonAsync("/_hub/case", new { id });
        answer.EnsureSuccessStatusCode();
        return (await answer.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>The hub's verdict on what was sent: the problems it saw, none when ok.</summary>
    public async Task<IReadOnlyList<string>> ProblemsAsync()
    {
        var verdict = (await _control.GetFromJsonAsync<JsonObject>("/_hub/verdict"))!;
        var problems = verdict["problems"]?.AsArray().Select(p => p!.ToJsonString()).ToList() ?? [];
        if (verdict["ok"]?.GetValue<bool>() != true && problems.Count == 0)
        {
            problems.Add("the verdict is not ok and names no problem");
        }
        return problems;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _control.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }
        _process?.Dispose();
        _process = null;
    }

    internal static JsonNode Read(string relative) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relative)))!;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
