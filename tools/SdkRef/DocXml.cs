using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MfxHub.SdkRef;

/// <summary>
/// The compiler's XML documentation file: each member's element by its documentation id, in the order the compiler
/// wrote them, which is the source's (file by file, declaration by declaration).
/// </summary>
internal sealed class DocXml
{
    private readonly Dictionary<string, XElement> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _order = new(StringComparer.Ordinal);

    public DocXml(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var index = 0;
        foreach (var member in document.Descendants("member"))
        {
            var name = (string?)member.Attribute("name");
            if (name is not null && _members.TryAdd(name, member))
            {
                _order[name] = index++;
            }
        }
    }

    /// <summary>The file next to an assembly, as the build writes it.</summary>
    public static DocXml For(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"no XML documentation beside {assembly.Location}: GenerateDocumentationFile is off", path);
        }
        return new DocXml(XDocument.Load(path, LoadOptions.PreserveWhitespace));
    }

    /// <summary>The member's element; null when it has none.</summary>
    public XElement? Element(string id) => _members.GetValueOrDefault(id);

    /// <summary>Where the member is in the source; members with no documentation come last.</summary>
    public int Order(string id) => _order.TryGetValue(id, out var at) ? at : int.MaxValue;

    /// <summary>A member's documentation id, as the compiler writes it (<c>M:MfxHub.Execution.PlaceOrderAsync(...)</c>).</summary>
    public static string Id(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);
        switch (member)
        {
            case Type type:
                return "T:" + TypeId(type);
            case MethodBase method:
            {
                var name = method.IsConstructor ? "#ctor" : method.Name.Replace('.', '#');
                if (method.IsGenericMethodDefinition)
                {
                    name += "``" + method.GetGenericArguments().Length;
                }
                var parameters = method.GetParameters();
                var list = parameters.Length == 0 ? string.Empty : "(" + string.Join(",", parameters.Select(p => ParamId(p.ParameterType))) + ")";
                return "M:" + TypeId(method.DeclaringType!) + "." + name + list;
            }
            case PropertyInfo property:
            {
                var parameters = property.GetIndexParameters();
                var list = parameters.Length == 0 ? string.Empty : "(" + string.Join(",", parameters.Select(p => ParamId(p.ParameterType))) + ")";
                return "P:" + TypeId(property.DeclaringType!) + "." + property.Name + list;
            }
            case FieldInfo field:
                return "F:" + TypeId(field.DeclaringType!) + "." + field.Name;
            case EventInfo @event:
                return "E:" + TypeId(@event.DeclaringType!) + "." + @event.Name;
            default:
                throw new ArgumentException($"no documentation id for a {member.MemberType}", nameof(member));
        }
    }

    private static string TypeId(Type type)
    {
        var outer = type.DeclaringType is { } declaring ? TypeId(declaring) + "." : (type.Namespace is { Length: > 0 } ns ? ns + "." : string.Empty);
        return outer + type.Name;
    }

    private static string ParamId(Type type)
    {
        if (type.IsByRef)
        {
            return ParamId(type.GetElementType()!) + "@";
        }
        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            return ParamId(type.GetElementType()!) + (rank == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", rank)) + "]");
        }
        if (type.IsPointer)
        {
            return ParamId(type.GetElementType()!) + "*";
        }
        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is null ? "`" : "``") + type.GenericParameterPosition;
        }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var outer = definition.DeclaringType is { } declaring ? TypeId(declaring) + "." : definition.Namespace + ".";
            return outer + CSharp.BareName(definition) + "{" + string.Join(",", type.GetGenericArguments().Select(ParamId)) + "}";
        }
        return TypeId(type);
    }
}

/// <summary>Where a cross-reference leads: the anchor, and the name the text shows.</summary>
internal sealed record Target(string Anchor, string Display, Type DeclaringType);

/// <summary>
/// XML documentation as Markdown. <c>see cref</c> to something in the file is <c>[Name](#anchor)</c>, and to
/// anything else a code span; <c>paramref</c>, <c>typeparamref</c> and <c>c</c> are code spans; <c>para</c> and
/// <c>code</c> are blocks. No HTML is written: a word with <c>&lt;</c> or <c>&gt;</c> outside code is put in a code span.
/// </summary>
internal sealed partial class Markdown(Func<string, Target?> resolve)
{
    /// <summary>The element's content as Markdown; empty when there is none.</summary>
    /// <param name="element">A <c>summary</c>, <c>remarks</c>, <c>param</c> or other documentation element.</param>
    /// <param name="context">The type being documented: a reference to one of its own members shows the member alone.</param>
    public string Convert(XElement? element, Type? context)
    {
        if (element is null)
        {
            return string.Empty;
        }
        var blocks = new List<string>();
        var inline = new StringBuilder();
        Walk(element, context, blocks, inline);
        Flush(blocks, inline);
        return string.Join("\n\n", blocks);
    }

    /// <summary>Several elements' Markdown, one after another as paragraphs.</summary>
    public string Join(Type? context, params XElement?[] elements) =>
        string.Join("\n\n", elements.Select(e => Convert(e, context)).Where(t => t.Length > 0));

    private void Walk(XElement element, Type? context, List<string> blocks, StringBuilder inline)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    inline.Append(Escape(text.Value));
                    break;
                case XElement child:
                    Element(child, context, blocks, inline);
                    break;
            }
        }
    }

    private void Element(XElement child, Type? context, List<string> blocks, StringBuilder inline)
    {
        switch (child.Name.LocalName)
        {
            case "para":
                Flush(blocks, inline);
                Walk(child, context, blocks, inline);
                Flush(blocks, inline);
                break;
            case "code":
                Flush(blocks, inline);
                blocks.Add("```csharp\n" + Dedent(child.Value) + "\n```");
                break;
            case "c":
                inline.Append(Code(Collapse(child.Value)));
                break;
            case "paramref":
            case "typeparamref":
                inline.Append(Code((string?)child.Attribute("name") ?? string.Empty));
                break;
            case "see":
            case "seealso":
                inline.Append(See(child, context));
                break;
            case "list":
                Flush(blocks, inline);
                foreach (var item in child.Elements("item"))
                {
                    var words = Convert(item.Element("description") ?? item, context);
                    var term = item.Element("term") is { } named ? Convert(named, context) : null;
                    blocks.Add("- " + (term is null ? words : term + ": " + words));
                }
                break;
            case "br":
                Flush(blocks, inline);
                break;
            default:
                Walk(child, context, blocks, inline);
                break;
        }
    }

    private string See(XElement see, Type? context)
    {
        if ((string?)see.Attribute("langword") is { } word)
        {
            return Code(word);
        }
        if ((string?)see.Attribute("href") is { } href)
        {
            var text = Collapse(see.Value);
            return "[" + (text.Length > 0 ? Escape(text) : href) + "](" + href + ")";
        }
        var cref = (string?)see.Attribute("cref") ?? string.Empty;
        var label = Collapse(see.Value);
        if (resolve(cref) is { } target)
        {
            var display = label.Length > 0
                ? label
                : target.DeclaringType == context && cref[0] != 'T' ? target.Display[(target.Display.LastIndexOf('.') + 1)..] : target.Display;
            return "[" + display + "](#" + target.Anchor + ")";
        }
        return Code(label.Length > 0 ? label : Unresolved(cref));
    }

    /// <summary>The name a reference outside the file shows: the type, or the type and member.</summary>
    internal static string Unresolved(string cref)
    {
        var id = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var kind = cref.Length > 2 && cref[1] == ':' ? cref[0] : 'T';
        var paren = id.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            id = id[..paren];
        }
        var parts = id.Split('.').Select(p => GenericArity().Replace(p, string.Empty)).ToArray();
        if (kind == 'T')
        {
            return parts[^1];
        }
        var member = parts[^1] == "#ctor" ? parts[^2] : parts[^1];
        return parts.Length >= 2 ? parts[^2] + "." + member : member;
    }

    private static void Flush(List<string> blocks, StringBuilder inline)
    {
        var text = Collapse(inline.ToString());
        if (text.Length > 0)
        {
            blocks.Add(text);
        }
        inline.Clear();
    }

    private static string Code(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }
        var fence = text.Contains('`', StringComparison.Ordinal) ? "``" : "`";
        var pad = fence.Length > 1 ? " " : string.Empty;
        return fence + pad + text + pad + fence;
    }

    /// <summary>Text outside code: a word holding an angle bracket goes in a code span, so no HTML can start in it.</summary>
    private static string Escape(string text) => Angled().Replace(text, m => Code(m.Value));

    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..].TrimEnd() : l.Trim()));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\S*[<>]\S*")]
    private static partial Regex Angled();

    [GeneratedRegex(@"`+\d+")]
    private static partial Regex GenericArity();
}
