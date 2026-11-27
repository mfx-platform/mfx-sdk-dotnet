using MfxHub.SdkRef;

// dotnet run --project tools/SdkRef -c Release -- [--check] [docs/sdkref.json]
//
// Writes the SDK reference from the built MfxHub.Sdk and the contract it vendors (spec/openapi.yaml). With
// --check it writes nothing and fails when the file differs from a fresh generation, or holds anything internal.
var check = args.Contains("--check");
var root = Repository.Root();
var target = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? Path.Combine(root, "docs", "sdkref.json"));
var bytes = Generator.Build(File.ReadAllText(Path.Combine(root, "spec", "openapi.yaml"))).ToJson();

var text = System.Text.Encoding.UTF8.GetString(bytes);
if (Generator.Internal().Match(text) is { Success: true } found)
{
    Console.Error.WriteLine($"the SDK reference would publish an internal word: \"{found.Value}\" at character {found.Index}; fix the doc comment it came from");
    return 1;
}

if (check)
{
    if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
    {
        Console.Error.WriteLine($"{target} is not what the generator writes: run `dotnet run --project tools/SdkRef -c Release` and commit it");
        return 1;
    }
    Console.WriteLine($"{target} is current");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(target)!);
File.WriteAllBytes(target, bytes);
Console.WriteLine($"wrote {target}");
return 0;

namespace MfxHub.SdkRef
{
    /// <summary>Where the repository is: the directory holding <c>MfxHub.Sdk.sln</c>.</summary>
    internal static class Repository
    {
        public static string Root(string? from = null)
        {
            for (var at = new DirectoryInfo(from ?? AppContext.BaseDirectory); at is not null; at = at.Parent)
            {
                if (File.Exists(Path.Combine(at.FullName, "MfxHub.Sdk.sln")))
                {
                    return at.FullName;
                }
            }
            throw new DirectoryNotFoundException("no MfxHub.Sdk.sln above " + (from ?? AppContext.BaseDirectory));
        }
    }
}
