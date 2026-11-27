using System.Globalization;
using System.Text;

namespace MfxHub.SdkRef;

/// <summary>A JSON object whose keys keep the order they were added in.</summary>
internal sealed class OrderedJson : List<KeyValuePair<string, object>>
{
    /// <summary>Adds a key; a value that is null is left out, as the format leaves out an empty optional key.</summary>
    public OrderedJson With(string key, object? value)
    {
        if (value is not null)
        {
            Add(new(key, value));
        }
        return this;
    }

    /// <summary>Adds a list when it holds something; the format never writes an empty one.</summary>
    public OrderedJson WithList<T>(string key, IReadOnlyList<T> items, Func<T, object> item) =>
        With(key, items.Count == 0 ? null : items.Select(item).ToList());
}

/// <summary>
/// JSON in the suite's canonical form: exactly what Python's <c>json.dumps(data, indent=2, ensure_ascii=False)</c>
/// writes, then one LF. Two-space indent, <c>": "</c> after a key, non-ASCII as itself, and only <c>"</c>,
/// <c>\</c> and the control characters escaped, those without a short form as lower-case <c>\u00xx</c>.
/// </summary>
internal static class CanonicalJson
{
    public static string Write(object value)
    {
        var text = new StringBuilder();
        Value(text, value, 0);
        return text.Append('\n').ToString();
    }

    private static void Value(StringBuilder text, object value, int depth)
    {
        switch (value)
        {
            case string s:
                String(text, s);
                break;
            case bool b:
                text.Append(b ? "true" : "false");
                break;
            case int n:
                text.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            case OrderedJson o:
                if (o.Count == 0)
                {
                    text.Append("{}");
                    break;
                }
                text.Append("{\n");
                for (var i = 0; i < o.Count; i++)
                {
                    Indent(text, depth + 1);
                    String(text, o[i].Key);
                    text.Append(": ");
                    Value(text, o[i].Value, depth + 1);
                    text.Append(i < o.Count - 1 ? ",\n" : "\n");
                }
                Indent(text, depth);
                text.Append('}');
                break;
            case System.Collections.IList list:
                if (list.Count == 0)
                {
                    text.Append("[]");
                    break;
                }
                text.Append("[\n");
                for (var i = 0; i < list.Count; i++)
                {
                    Indent(text, depth + 1);
                    Value(text, list[i]!, depth + 1);
                    text.Append(i < list.Count - 1 ? ",\n" : "\n");
                }
                Indent(text, depth);
                text.Append(']');
                break;
            default:
                throw new ArgumentException($"no JSON for a {value.GetType().Name}", nameof(value));
        }
    }

    private static void Indent(StringBuilder text, int depth) => text.Append(' ', depth * 2);

    private static void String(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                case < ' ':
                    text.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    text.Append(ch);
                    break;
            }
        }
        text.Append('"');
    }
}

/// <summary>The whole file: format <c>mfx-sdkref/1</c>.</summary>
internal sealed record RefDocument(
    string Language, string Package, string Version, string Install, IReadOnlyList<(string Name, string Url)> Links,
    IReadOnlyList<RefPage> Pages)
{
    /// <summary>The format this writes.</summary>
    public const string Format = "mfx-sdkref/1";

    /// <summary>The file's bytes: UTF-8 without a byte order mark, in the canonical form, keys in the format's order.</summary>
    public byte[] ToJson() => new UTF8Encoding(false).GetBytes(ToString());

    /// <summary>The file as text.</summary>
    public override string ToString()
    {
        var links = new OrderedJson();
        foreach (var (name, url) in Links)
        {
            links.With(name, url);
        }
        var root = new OrderedJson()
            .With("format", Format)
            .With("language", Language)
            .With("package", Package)
            .With("version", Version)
            .With("install", Install)
            .With("links", links.Count == 0 ? null : links)
            .WithList("pages", Pages, p => p.ToJson());
        return CanonicalJson.Write(root);
    }
}

/// <summary>One page: its id, this language's introduction and its types.</summary>
internal sealed record RefPage(string Id, string Intro, IReadOnlyList<RefType> Types)
{
    public OrderedJson ToJson() => new OrderedJson()
        .With("id", Id)
        .With("intro", Intro)
        .With("types", Types.Select(t => (object)t.ToJson()).ToList());
}

/// <summary>One public type; <see cref="Schema"/> is the contract schema it represents, when it represents one.</summary>
internal sealed record RefType(
    string Anchor, string Kind, string Name, string Signature, string Doc, IReadOnlyList<string> Extends, string? Code,
    string? Schema, IReadOnlyList<RefMember> Members)
{
    public OrderedJson ToJson() => new OrderedJson()
        .With("anchor", Anchor)
        .With("kind", Kind)
        .With("name", Name)
        .With("signature", Signature)
        .With("doc", Doc)
        .WithList("extends", Extends, e => e)
        .With("code", string.IsNullOrEmpty(Code) ? null : Code)
        .With("schema", string.IsNullOrEmpty(Schema) ? null : Schema)
        .WithList("members", Members, m => m.ToJson());
}

/// <summary>One public member of a type; overloads are one member.</summary>
internal sealed record RefMember(
    string Anchor, string Kind, string Name, string Signature, string Doc, IReadOnlyList<RefParam> Params,
    RefReturns? Returns, IReadOnlyList<RefRaise> Raises, string? Operation, string? Deprecated)
{
    public OrderedJson ToJson() => new OrderedJson()
        .With("anchor", Anchor)
        .With("kind", Kind)
        .With("name", Name)
        .With("signature", Signature)
        .With("doc", Doc)
        .WithList("params", Params, p => new OrderedJson()
            .With("name", p.Name)
            .With("type", p.Type)
            .With("default", p.Default)
            .With("doc", p.Doc))
        .With("returns", Returns is null ? null : new OrderedJson().With("type", Returns.Type).With("doc", Returns.Doc))
        .WithList("raises", Raises, r => new OrderedJson().With("type", r.Type).With("doc", r.Doc))
        .With("operation", string.IsNullOrEmpty(Operation) ? null : Operation)
        .With("deprecated", string.IsNullOrEmpty(Deprecated) ? null : Deprecated);
}

/// <summary>One parameter: its name, type, default as C# writes it (none when required) and doc.</summary>
internal sealed record RefParam(string Name, string Type, string? Default, string Doc);

/// <summary>What a member returns.</summary>
internal sealed record RefReturns(string Type, string Doc);

/// <summary>What a member throws, from its <c>exception</c> tags.</summary>
internal sealed record RefRaise(string Type, string Doc);
