using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MfxHub.SdkRef;

/// <summary>One operation of the contract: its method, path template and operationId.</summary>
internal sealed record ContractOperation(string Method, string Path, string OperationId)
{
    private readonly Regex _pattern = new(
        "^" + Regex.Replace(Regex.Escape(Path), @"\\\{[^/]+?}", "[^/]+") + "$", RegexOptions.CultureInvariant);

    /// <summary>How many of the template's segments are literal: the more, the closer the match.</summary>
    public int Literals => Path.Split('/').Count(s => s.Length > 0 && !s.StartsWith('{'));

    public bool Matches(string method, string path) =>
        string.Equals(method, Method, StringComparison.OrdinalIgnoreCase) && _pattern.IsMatch(path);
}

/// <summary>
/// Which contract operation each method calls. Derived from the route: each method of a domain client is called
/// against a recording handler that answers nothing usable, and the first request it sends is matched against the
/// contract's paths (<c>spec/openapi.yaml</c>). What sends no HTTP request of its own is named in <see cref="Fixed"/>.
/// </summary>
internal static partial class Operations
{
    /// <summary>
    /// The operations no recorded request shows: the stream is opened over a WebSocket, whose connector is the
    /// caller's to replace.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Fixed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MfxHub.MfxClient.StreamAsync"] = "openStream",
    };

    private const string Base = "https://api.example.test/v1";

    /// <summary>Every operation in an OpenAPI document's <c>paths</c>, read line by line (the contract's own layout).</summary>
    public static IReadOnlyList<ContractOperation> ReadContract(string openapiYaml)
    {
        ArgumentNullException.ThrowIfNull(openapiYaml);
        var found = new List<ContractOperation>();
        var inPaths = false;
        string? path = null;
        string? method = null;
        foreach (var raw in openapiYaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.StartsWith("paths:", StringComparison.Ordinal))
            {
                inPaths = true;
                continue;
            }
            if (!inPaths)
            {
                continue;
            }
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && !raw.StartsWith('#'))
            {
                break;
            }
            if (PathLine().Match(raw) is { Success: true } p)
            {
                path = p.Groups[1].Value;
                method = null;
            }
            else if (MethodLine().Match(raw) is { Success: true } m)
            {
                method = m.Groups[1].Value.ToUpperInvariant();
            }
            else if (IdLine().Match(raw) is { Success: true } id && path is not null && method is not null)
            {
                found.Add(new ContractOperation(method, path, id.Groups[1].Value));
            }
        }
        return found;
    }

    /// <summary>The operation a request goes to; null when no path of the contract takes it.</summary>
    public static ContractOperation? Match(IReadOnlyList<ContractOperation> contract, string method, string path) =>
        contract.Where(o => o.Matches(method, path)).OrderByDescending(o => o.Literals).FirstOrDefault();

    /// <summary>
    /// The request <paramref name="method"/> sends first, as <c>(METHOD, /path)</c> below the base URL; null when it
    /// sends none however it is called.
    /// </summary>
    /// <param name="method">A public instance method of a domain client (a type <see cref="MfxClient"/> exposes).</param>
    /// <param name="resource">The property of <see cref="MfxClient"/> the client is reached as.</param>
    public static (string Method, string Path)? FirstRequest(MethodInfo method, PropertyInfo resource)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(resource);
        // Optional arguments as declared first; then every one given, for the calls that need one of them; then
        // each given alone, for the calls that refuse two together.
        var optional = method.GetParameters().Where(p => p.IsOptional).Select(p => p.Position).ToList();
        var attempts = new List<HashSet<int>> { new(), new(optional) };
        attempts.AddRange(optional.Select(p => new HashSet<int> { p }));
        foreach (var given in attempts)
        {
            if (Record(method, resource, given) is { } sent)
            {
                return sent;
            }
        }
        return null;
    }

    private static (string Method, string Path)? Record(MethodInfo method, PropertyInfo resource, HashSet<int> given)
    {
        using var handler = new Recorder();
        using var client = new MfxClient(new Uri(Base), "mfxk_sdkref", SigningKey.FromSeed(new byte[32]), new MfxClientOptions
        {
            Handler = handler,
            ReadRetries = 0,
            Pause = (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                handler.Stop.Cancel();
                throw new OperationCanceledException(handler.Stop.Token);
            },
        });
        var target = resource.GetValue(client);
        object?[] arguments;
        try
        {
            arguments = method.GetParameters()
                .Select(p => p.ParameterType == typeof(CancellationToken)
                    ? handler.Stop.Token
                    : p.IsOptional && !given.Contains(p.Position) ? Type.Missing :Sample.For(p.ParameterType, p.Name ?? string.Empty))
                .ToArray();
        }
        catch (NotSupportedException)
        {
            return null;
        }
        Run(method, target, arguments);
        return handler.First;
    }

#pragma warning disable CA1031 // every failure of a call made only to see its request is expected and ignored
    private static void Run(MethodInfo method, object? target, object?[] arguments)
    {
        try
        {
            var result = method.Invoke(target, arguments);
            var pending = result switch
            {
                Task task => task,
                ValueTask task => task.AsTask(),
                _ when result is not null && AsyncEnumerable(result.GetType()) is { } element => FirstOf(result, element),
                _ => Task.CompletedTask,
            };
            pending.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
        }
    }
#pragma warning restore CA1031

    private static Type? AsyncEnumerable(Type type) =>
        type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))?
            .GetGenericArguments()[0];

    private static Task FirstOf(object sequence, Type element) =>
        (Task)typeof(Operations).GetMethod(nameof(FirstItem), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(element).Invoke(null, [sequence])!;

    private static async Task FirstItem<T>(IAsyncEnumerable<T> sequence)
    {
        await using var items = sequence.GetAsyncEnumerator();
        await items.MoveNextAsync().ConfigureAwait(false);
    }

    /// <summary>Records the first request and answers every one with a 500, so no call goes further than its first.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public CancellationTokenSource Stop { get; } = new();

        public (string Method, string Path)? First { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            First ??= (request.Method.Method, path.StartsWith("/v1/", StringComparison.Ordinal) ? path[3..] : path);
            Stop.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":{"code":"internal_error","message":"sdkref"}}""", Encoding.UTF8, "application/json"),
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [GeneratedRegex(@"^  (/\S*):\s*$")]
    private static partial Regex PathLine();

    [GeneratedRegex(@"^    (get|put|post|delete|patch|head|options):\s*$")]
    private static partial Regex MethodLine();

    [GeneratedRegex(@"^      operationId:\s*['""]?([A-Za-z0-9_]+)['""]?\s*$")]
    private static partial Regex IdLine();
}

/// <summary>An argument of each type that the SDK's own checks accept, so a call gets as far as its request.</summary>
internal static class Sample
{
    private static readonly Guid Id = new("0191f0c2-aaaa-7bbb-8ccc-000000000001");
    private static readonly DateTimeOffset At = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static object? For(Type type, string name, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (depth > 3)
        {
            return null;
        }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return For(underlying, name, depth);
        }
        if (type == typeof(string))
        {
            return Text(name);
        }
        if (type == typeof(Guid))
        {
            return Id;
        }
        if (type == typeof(bool))
        {
            return true;
        }
        if (type == typeof(DateTimeOffset))
        {
            return At;
        }
        if (type == typeof(DateTime))
        {
            return At.UtcDateTime;
        }
        if (type == typeof(DateOnly))
        {
            return DateOnly.FromDateTime(At.UtcDateTime);
        }
        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromSeconds(1);
        }
        if (type == typeof(Uri))
        {
            return new Uri("https://hooks.example.test/mfx");
        }
        if (type.IsEnum)
        {
            return Enum.GetValues(type).GetValue(0);
        }
        if (type.IsPrimitive || type == typeof(decimal))
        {
            return System.Convert.ChangeType(1, type, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (type == typeof(Secret))
        {
            return new Secret("sdkref-secret");
        }
        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>))
        {
            byte[] bytes = [1, 2, 3];
            return type == typeof(byte[]) ? bytes : new ReadOnlyMemory<byte>(bytes);
        }
        if (type.IsArray)
        {
            var array = Array.CreateInstance(type.GetElementType()!, 1);
            array.SetValue(For(type.GetElementType()!, name, depth + 1), 0);
            return array;
        }
        if (type.IsGenericType && Dictionary(type) is { } pair)
        {
            var map = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(pair.Key, pair.Value))!;
            map.Add(For(pair.Key, "key", depth + 1)!, For(pair.Value, "value", depth + 1));
            return map;
        }
        if (type.IsGenericType && Sequence(type) is { } element)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            list.Add(For(element, name, depth + 1));
            return list;
        }
        if (typeof(Delegate).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
        {
            return null;
        }
        if (type.GetProperty("Keep", BindingFlags.Public | BindingFlags.Static) is { } keep && keep.PropertyType == type)
        {
            return keep.GetValue(null);
        }
        return Instance(type, depth);
    }

    private static string Text(string name) => name.ToUpperInvariant() switch
    {
        var n when n.Contains("IDEMPOTENCY", StringComparison.Ordinal) => "sdkref-key-1",
        var n when n.Contains("TIMEFRAME", StringComparison.Ordinal) => "M1",
        var n when n.Contains("CURRENCY", StringComparison.Ordinal) => "USD",
        var n when n.Contains("CHANNEL", StringComparison.Ordinal) => "events",
        var n when n.Contains("URL", StringComparison.Ordinal) => "https://hooks.example.test/mfx",
        var n when n.Contains("MONTH", StringComparison.Ordinal) || n.Contains("PERIOD", StringComparison.Ordinal) => "2026-01",
        var n when n.Contains("DATE", StringComparison.Ordinal) || n.Contains("DAY", StringComparison.Ordinal) => "2026-01-05",
        var n when n.Contains("ID", StringComparison.Ordinal) => Id.ToString(),
        _ => "x",
    };

    private static KeyValuePair<Type, Type>? Dictionary(Type type)
    {
        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(Dictionary<,>))
        {
            var arguments = type.GetGenericArguments();
            return new(arguments[0], arguments[1]);
        }
        return null;
    }

    private static Type? Sequence(Type type)
    {
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IList<>) || definition == typeof(ICollection<>) || definition == typeof(List<>)
            ? type.GetGenericArguments()[0]
            : null;
    }

    private static object? Instance(Type type, int depth)
    {
        if (type.GetConstructor(Type.EmptyTypes) is { } empty)
        {
            var made = empty.Invoke(null);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is { IsPublic: true }))
            {
                property.SetValue(made, For(property.PropertyType, property.Name, depth + 1));
            }
            return made;
        }
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new NotSupportedException($"no way to make a {type.Name}");
        return constructor.Invoke(constructor.GetParameters().Select(p => For(p.ParameterType, p.Name ?? string.Empty, depth + 1)).ToArray());
    }
}
