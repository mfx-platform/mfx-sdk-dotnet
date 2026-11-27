using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MfxHub.SdkRef;

/// <summary>
/// Builds <c>docs/sdkref.json</c> (format <c>mfx-sdkref/1</c>) from MfxHub.Sdk's public API: every public type of
/// the <c>MfxHub</c> namespace, on the page its area belongs to, with its public members, their C# signatures and
/// their documentation as Markdown.
/// </summary>
/// <remarks>
/// A type that represents one contract object names it in <c>schema</c>, the key of <c>components.schemas</c> its
/// <c>ContractSchema</c> attribute carries, and the docs site links it to that object's fields. The generated models
/// (<c>MfxHub.Models</c>) are listed only when a listed signature names them, each on the first page (in page order)
/// whose signatures name it, after that page's own types: an entry with its schema and no members, because the
/// API reference documents the object's fields once for every language. A model that names no schema is not listed.
/// </remarks>
internal static partial class Generator
{
    /// <summary>The pages, in the format's fixed order.</summary>
    public static readonly IReadOnlyList<string> PageOrder =
    [
        "client", "terminals", "execution", "trading-accounts", "accounts", "market-data", "stream", "risk", "copy",
        "billing", "tenant", "errors",
    ];

    /// <summary>Each page's introduction, for C#.</summary>
    public static readonly IReadOnlyDictionary<string, string> Intros = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["client"] = "`MfxClient` signs every request with your Ed25519 key, retries a rate-limited read after the platform's "
            + "own `Retry-After`, never retries a write, and opens the stream. Each area of the API is a property of the "
            + "client (`client.Terminals`, `client.Execution` and so on). A `Secret` or a `SigningKey` prints as `***`.",
        ["terminals"] = "Provision, inspect, restart, drain, resume and retire the terminals your tenant owns "
            + "(`client.Terminals`); the farms they run on (`client.Farms`), the maintenance windows that reach them "
            + "(`client.Maintenance`), and the expert advisor versions you upload for them (`client.ExpertAdvisors`).",
        ["execution"] = "Order entry (`client.Execution`). Every method returns an `ExecutionResult` and none throws for an "
            + "outcome: a timeout, a transport fault and a broker rejection are all results with a reason. Prices and "
            + "volumes are `decimal`.",
        ["trading-accounts"] = "Trading accounts over every venue (`client.TradingAccounts`): reads, the same order entry "
            + "as `client.Execution`, and the cTrader grant flow.",
        ["accounts"] = "What a terminal sees of its account (`client.Accounts`), the brokers, servers and symbols you can "
            + "point a terminal at (`client.Catalog`), and what your key has used (`client.Usage`).",
        ["market-data"] = "Streamed market data subscriptions, candle and tick history, and the order book (`client.Md`).",
        ["stream"] = "One WebSocket, many channels, resumed by sequence number after a reconnect (`client.StreamAsync`), the "
            + "typed events it carries, and `LiveAccount`, a view of an account kept current from them.",
        ["risk"] = "Risk Management (`client.Risk`): policies, protected accounts, the planner, exit plans, challenges and "
            + "activity, and the risk events on the stream.",
        ["copy"] = "Copy Trading (`client.Copy`): leaders, followers, groups, stop-outs, requested closes, signals and the "
            + "copy log, and the copy events on the stream.",
        ["billing"] = "Billing, read only (`client.Billing`): standing, profiles, forecast, prices and invoices.",
        ["tenant"] = "What your tenant reads about itself (`client.Tenant`), and its webhook endpoints (`client.Webhooks`) "
            + "with `Webhooks.Verify` for a delivery.",
        ["errors"] = "Every exception the SDK throws derives from `MfxException`. A failure the platform describes is an "
            + "`MfxApiException` typed by the platform's error code, or by the HTTP status when the code is one this "
            + "version does not know. Execution methods never throw for an outcome; they return a result.",
    };

    /// <summary>The namespace of the generated models.</summary>
    public const string ModelsNamespace = "MfxHub.Models";

    /// <summary>
    /// The page of every public type that is not an exception (exceptions are all on <c>errors</c>). A public type
    /// missing here stops the generator, so a new type is placed on purpose.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PageOf = Pages(
        ("client", ["MfxClient", "MfxClientOptions", "Budget", "CallTiming", "Signing", "SigningKey", "RequestSigner", "Secret",
            "Page`1", "MfxJson", "ExactDecimalConverter", "ExactNullableDecimalConverter"]),
        ("terminals", ["Terminals", "Farms", "Maintenance", "ExpertAdvisors", "EaRecord", "EaTelemetry", "EaTelemetryLost"]),
        ("execution", ["Execution", "Expiry", "Side", "OrderType", "Reason", "Tickets", "VenueDeal", "ExecutionResult"]),
        ("trading-accounts", ["TradingAccounts"]),
        ("accounts", ["Accounts", "Catalog", "UsageReader"]),
        ("market-data", ["MarketData", "SubscriptionDegraded", "SubscriptionRecovered", "Book", "CandleClosed"]),
        ("stream", ["MfxStream", "StreamOptions", "IStreamConnection", "StreamConnector", "Channel", "StreamEvent", "StreamMessage",
            "Resync", "ChannelRefused", "StreamStats", "LiveAccount", "AccountView", "MfxEvent", "OrderEvent", "OrderPlaced",
            "OrderModified", "OrderClosed", "PositionEvent", "PositionOpened", "PositionModified", "PositionClosed",
            "TerminalStateChanged"]),
        ("risk", ["Risk", "CoverageHealth", "RiskAccountSort", "TimeSort", "RiskActivitySort", "RiskChallengeRule",
            "RiskChallengeSort", "RiskRuleEvent", "RiskCoverageChanged", "RiskProgrammeResult", "RiskPeriodClosed"]),
        ("copy", ["Copy", "CopyLogSort", "CopyRolloverField", "Change`1", "CopyDeal", "CopyAs", "CopyClosed", "CopyNotClosed",
            "CopyFollowerEvent", "CopyCopied", "CopyRefused", "CopyClosedEvent", "CopyStopoutEvent", "CopyDrift", "CopyStateChanged"]),
        ("billing", ["Billing"]),
        ("tenant", ["Tenant", "Webhooks"]),
        ("errors", ["RiskRefusal", "PaymentAction", "DueDocument"]));

    /// <summary>Object's own members, which every type has and no page lists.</summary>
    private static readonly HashSet<string> ObjectMembers = new(StringComparer.Ordinal) { "ToString", "Equals", "GetHashCode", "GetType" };

    private static Dictionary<string, string> Pages(params (string Page, string[] Types)[] pages) =>
        pages.SelectMany(p => p.Types.Select(t => (t, p.Page))).ToDictionary(p => p.t, p => p.Page, StringComparer.Ordinal);

    /// <summary>The document for MfxHub.Sdk, against the contract the SDK vendors.</summary>
    /// <param name="openapiYaml">The contract (<c>spec/openapi.yaml</c>), whose operationIds the members call.</param>
    public static RefDocument Build(string openapiYaml)
    {
        var assembly = typeof(MfxClient).Assembly;
        var builder = new Builder(assembly, DocXml.For(assembly), Operations.ReadContract(openapiYaml));
        return builder.Build();
    }

    /// <summary>The SDK's version, as its <c>User-Agent</c> names it.</summary>
    public static string Version(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var field = assembly.GetType("MfxHub.SdkVersion", throwOnError: true)!.GetField("Value", BindingFlags.Public | BindingFlags.Static)!;
        return (string)field.GetRawConstantValue()!;
    }

    /// <summary>
    /// The contract schema <paramref name="type"/> represents, as its <c>ContractSchema</c> attribute names it (a key
    /// of <c>components.schemas</c>, case kept); null for a type that represents none.
    /// </summary>
    public static string? SchemaOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "MfxHub.ContractSchemaAttribute")
            .Select(a => (string?)a.ConstructorArguments[0].Value)
            .SingleOrDefault();
    }

    /// <summary>A listed model's doc: what it is generated from, whose fields the docs site links to.</summary>
    public static string ModelDoc(string schema) => $"Generated from the API's `{schema}` object, in the namespace `{ModelsNamespace}`.";

    /// <summary>The platform's error code each exception type stands for, from the SDK's own mapping.</summary>
    public static IReadOnlyDictionary<Type, string> ErrorCodes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var errors = assembly.GetType("MfxHub.ApiErrors", throwOnError: true)!;
        var byCode = (IReadOnlyDictionary<string, Type>)errors.GetField("ByCodeTypes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return byCode.ToDictionary(p => p.Value, p => p.Key);
    }

    private sealed class Builder
    {
        private readonly Assembly _assembly;
        private readonly DocXml _xml;
        private readonly IReadOnlyList<ContractOperation> _contract;
        private readonly Nullability _nullability = new();
        private readonly Markdown _markdown;
        private readonly Dictionary<string, Target> _targets = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<Type, string> _codes;
        private readonly Dictionary<Type, PropertyInfo> _resources;

        public Builder(Assembly assembly, DocXml xml, IReadOnlyList<ContractOperation> contract)
        {
            _assembly = assembly;
            _xml = xml;
            _contract = contract;
            _markdown = new Markdown(cref => _targets.GetValueOrDefault(cref));
            _codes = ErrorCodes(assembly);
            _resources = typeof(MfxClient).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.Assembly == assembly && p.PropertyType.GetConstructors().Length == 0
                    && p.PropertyType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Any(c => c.GetParameters() is [{ ParameterType: var t }] && t == typeof(MfxClient)))
                .ToDictionary(p => p.PropertyType);
        }

        public RefDocument Build()
        {
            var types = _assembly.GetExportedTypes()
                .Where(t => t.Namespace == "MfxHub" && !t.IsNested)
                .OrderBy(t => _xml.Order(DocXml.Id(t))).ThenBy(t => t.MetadataToken)
                .ToList();
            var placed = types.Select(t => (Type: t, Page: Page(t))).ToList();

            // Every anchor first, so a reference to a type further down still links.
            var plans = placed.Select(p => (p.Type, p.Page, Members: Members(p.Type).ToList())).ToList();
            plans.AddRange(Models(plans).Select(m => (m.Type, m.Page, Members: new List<MemberInfo>())));
            foreach (var (type, page, members) in plans)
            {
                var typeAnchor = page + "." + CSharp.BareName(type);
                _targets[DocXml.Id(type)] = new Target(typeAnchor, CSharp.BareName(type), type);
                foreach (var (name, anchor, overloads) in Groups(type, typeAnchor, members))
                {
                    foreach (var member in overloads)
                    {
                        _targets[DocXml.Id(member)] = new Target(anchor, CSharp.BareName(type) + "." + name, type);
                    }
                }
            }

            var pages = PageOrder
                .Select(id => new RefPage(id, Intros[id], plans.Where(p => p.Page == id).Select(p => Type(p.Type, p.Page, p.Members)).ToList()))
                .Where(p => p.Types.Count > 0)
                .ToList();
            return new RefDocument(
                "csharp", "MfxHub.Sdk", Version(_assembly), "dotnet add package MfxHub.Sdk",
                [("source", "https://github.com/mfx-platform/mfx-sdk-dotnet")], pages);
        }

        /// <summary>
        /// The generated models that name a contract schema and that a listed signature names (a member's type, a
        /// parameter's, a return type, a base type, as themselves or inside a generic, array or nullable), each on
        /// the first page in page order whose signatures name it, in the order they are first named there.
        /// </summary>
        private static List<(Type Type, string Page)> Models(List<(Type Type, string Page, List<MemberInfo> Members)> plans)
        {
            var found = new List<(Type, string)>();
            var seen = new HashSet<Type>();
            foreach (var page in PageOrder)
            {
                var onPage = plans.Where(p => p.Page == page).ToList();
                var anchors = onPage.Select(p => CSharp.BareName(p.Type)).ToHashSet(StringComparer.Ordinal);
                foreach (var (type, _, members) in onPage)
                {
                    foreach (var named in Named(type, members).SelectMany(Parts))
                    {
                        if (named.Namespace != ModelsNamespace || SchemaOf(named) is null || !seen.Add(named))
                        {
                            continue;
                        }
                        if (!anchors.Add(CSharp.BareName(named)))
                        {
                            throw new InvalidOperationException($"the model {named.FullName} would take the anchor {page}.{CSharp.BareName(named)}, which another type has");
                        }
                        found.Add((named, page));
                    }
                }
            }
            return found;
        }

        /// <summary>Every type a listed type's signatures name, in the order the page shows them.</summary>
        private static IEnumerable<Type> Named(Type type, IReadOnlyList<MemberInfo> members)
        {
            if (type.BaseType is { } baseType)
            {
                yield return baseType;
            }
            foreach (var contract in type.GetInterfaces())
            {
                yield return contract;
            }
            if (typeof(Delegate).IsAssignableFrom(type) && type.GetMethod("Invoke") is { } invoke)
            {
                yield return invoke.ReturnType;
                foreach (var parameter in invoke.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }
            if (PrimaryConstructor(type) is { } primary)
            {
                foreach (var parameter in primary.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }
            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldInfo field:
                        yield return field.FieldType;
                        break;
                    case PropertyInfo property:
                        yield return property.PropertyType;
                        foreach (var parameter in property.GetIndexParameters())
                        {
                            yield return parameter.ParameterType;
                        }
                        break;
                    case EventInfo handler:
                        yield return handler.EventHandlerType!;
                        break;
                    case MethodBase method:
                        if (method is MethodInfo info)
                        {
                            yield return info.ReturnType;
                        }
                        foreach (var parameter in method.GetParameters())
                        {
                            yield return parameter.ParameterType;
                        }
                        break;
                }
            }
        }

        /// <summary>A type and every type inside it: an array's or a reference's element, a generic's arguments.</summary>
        private static IEnumerable<Type> Parts(Type type)
        {
            yield return type;
            if (type.HasElementType)
            {
                foreach (var part in Parts(type.GetElementType()!))
                {
                    yield return part;
                }
            }
            if (type.IsGenericType)
            {
                foreach (var part in type.GetGenericArguments().SelectMany(Parts))
                {
                    yield return part;
                }
            }
        }

        private static string Page(Type type) =>
            typeof(Exception).IsAssignableFrom(type) ? "errors"
            : PageOf.TryGetValue(type.Name, out var page) ? page
            : throw new InvalidOperationException($"the public type {type.FullName} is on no page: add it to Generator.PageOf");

        /// <summary>
        /// The members by name, in the order each name first appears: overloads are one entry with one anchor (the
        /// format's anchors are unique), shown by the overload with the most parameters.
        /// </summary>
        private static List<(string Name, string Anchor, List<MemberInfo> Overloads)> Groups(Type type, string typeAnchor, IReadOnlyList<MemberInfo> members) =>
            members.GroupBy(m => Name(type, m), StringComparer.Ordinal)
                .Select(g => (g.Key, typeAnchor + "." + g.Key, g.ToList()))
                .ToList();

        private static string Name(Type type, MemberInfo member) => member switch
        {
            ConstructorInfo => CSharp.BareName(type),
            PropertyInfo { Name: "Item" } p when p.GetIndexParameters().Length > 0 => "Item",
            _ => member.Name,
        };

        /// <summary>The members a page lists, in the source's order.</summary>
        private IEnumerable<MemberInfo> Members(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (type.IsEnum)
            {
                return type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.MetadataToken);
            }
            if (typeof(Delegate).IsAssignableFrom(type))
            {
                return [];
            }
            var primary = PrimaryConstructor(type);
            var members = new List<MemberInfo>();
            members.AddRange(type.GetConstructors(flags).Where(c => c != primary && !(type.IsAbstract && c.GetParameters().Length == 0 && CSharp.CompilerWritten(c))));
            members.AddRange(type.GetFields(flags).Where(f => !CSharp.CompilerWritten(f)));
            members.AddRange(type.GetProperties(flags).Where(p => p.GetMethod is { IsPublic: true } || p.SetMethod is { IsPublic: true })
                .Where(p => p.Name != "EqualityContract"));
            members.AddRange(type.GetEvents(flags));
            members.AddRange(type.GetMethods(flags).Where(m => !m.IsSpecialName && !CSharp.CompilerWritten(m) && !ObjectMembers.Contains(m.Name)));
            return members.OrderBy(m => _xml.Order(DocXml.Id(m))).ThenBy(m => m.MetadataToken);
        }

        /// <summary>A positional record's primary constructor, which its signature shows; null for any other type.</summary>
        private static ConstructorInfo? PrimaryConstructor(Type type)
        {
            if (!CSharp.IsRecord(type))
            {
                return null;
            }
            var deconstruct = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == "Deconstruct" && CSharp.CompilerWritten(m));
            if (deconstruct is null)
            {
                return null;
            }
            var names = deconstruct.GetParameters().Select(p => p.Name).ToList();
            return type.GetConstructors().FirstOrDefault(c => c.GetParameters().Select(p => p.Name).SequenceEqual(names));
        }

        private RefType Type(Type type, string page, IReadOnlyList<MemberInfo> members)
        {
            var isError = typeof(Exception).IsAssignableFrom(type);
            var schema = SchemaOf(type);
            if (type.Namespace == ModelsNamespace)
            {
                // The object's fields and their documentation are the API reference's, once for every language.
                return new RefType(
                    page + "." + CSharp.BareName(type), type.IsEnum ? "enum" : "class", CSharp.DeclaredName(type), TypeSignature(type),
                    ModelDoc(schema!), Extends(type), null, schema, []);
            }
            var element = Documented(type);
            var kind = isError ? "error"
                : type.IsEnum ? "enum"
                : type.IsInterface ? "interface"
                : typeof(Delegate).IsAssignableFrom(type) ? "alias"
                : CSharp.IsRecord(type) ? "record"
                : "class";
            var groups = Groups(type, page + "." + CSharp.BareName(type), members);
            return new RefType(
                page + "." + CSharp.BareName(type),
                kind,
                CSharp.DeclaredName(type),
                TypeSignature(type),
                _markdown.Join(type, element?.Element("summary"), element?.Element("remarks")),
                Extends(type),
                isError ? _codes.GetValueOrDefault(type) : null,
                schema,
                groups.Select(g => Overloaded(type, g.Name, g.Anchor, g.Overloads)).ToList());
        }

        private string TypeSignature(Type type)
        {
            if (typeof(Delegate).IsAssignableFrom(type))
            {
                var invoke = type.GetMethod("Invoke")!;
                return "public delegate " + CSharp.TypeName(invoke.ReturnType, _nullability.Create(invoke.ReturnParameter)) + " "
                    + CSharp.DeclaredName(type) + CSharp.Parameters(invoke.GetParameters(), _nullability);
            }
            var words = new List<string> { "public" };
            if (type.IsClass && type.IsAbstract && type.IsSealed)
            {
                words.Add("static");
            }
            else if (type.IsClass && type.IsAbstract)
            {
                words.Add("abstract");
            }
            else if (type.IsClass && type.IsSealed)
            {
                words.Add("sealed");
            }
            words.Add(type.IsEnum ? "enum" : type.IsInterface ? "interface" : CSharp.IsRecord(type) ? "record" : type.IsValueType ? "struct" : "class");
            var signature = string.Join(' ', words) + " " + CSharp.DeclaredName(type);
            if (PrimaryConstructor(type) is { } primary)
            {
                signature += CSharp.Parameters(primary.GetParameters(), _nullability);
            }
            var bases = Extends(type);
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                bases = underlying == typeof(int) ? [] : [CSharp.TypeName(underlying)];
            }
            if (bases.Count > 0)
            {
                signature += " : " + string.Join(", ", bases);
            }
            if (type.IsGenericTypeDefinition)
            {
                signature += CSharp.Constraints(type.GetGenericArguments());
            }
            return signature;
        }

        /// <summary>The base class and the interfaces the type itself declares.</summary>
        private static List<string> Extends(Type type)
        {
            var names = new List<string>();
            if (type.IsEnum || typeof(Delegate).IsAssignableFrom(type))
            {
                return names;
            }
            if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType))
            {
                names.Add(CSharp.TypeName(baseType));
            }
            var inherited = type.BaseType?.GetInterfaces() ?? [];
            var all = type.GetInterfaces().Where(i => !inherited.Contains(i)).ToList();
            var implied = all.SelectMany(i => i.GetInterfaces()).ToHashSet();
            names.AddRange(all
                .Where(i => !implied.Contains(i))
                .Where(i => !(CSharp.IsRecord(type) && i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEquatable<>) && i.GetGenericArguments()[0] == type))
                .OrderBy(i => i.MetadataToken)
                .Select(i => CSharp.TypeName(i)));
            return names;
        }

        /// <summary>
        /// One entry for every overload of a name: the overload with the most parameters (the first declared, on a
        /// tie) gives the signature, parameters, returns and operation, and the doc ends by listing every overload's
        /// full signature.
        /// </summary>
        private RefMember Overloaded(Type type, string name, string anchor, List<MemberInfo> overloads)
        {
            var shown = overloads.OrderByDescending(Arity).First();
            var entry = Member(type, shown, name, anchor);
            if (overloads.Count == 1)
            {
                return entry;
            }
            var list = string.Join("\n", overloads.Select(o => "- `" + Member(type, o, name, anchor).Signature + "`"));
            var doc = (entry.Doc.Length > 0 ? entry.Doc + "\n\n" : string.Empty) + "Overloads:\n\n" + list;
            return entry with { Doc = doc };
        }

        private static int Arity(MemberInfo member) => member switch
        {
            MethodBase method => method.GetParameters().Length,
            PropertyInfo property => property.GetIndexParameters().Length,
            _ => 0,
        };

        private RefMember Member(Type type, MemberInfo member, string name, string anchor)
        {
            var element = Documented(member);
            var doc = _markdown.Join(type, element?.Element("summary"), element?.Element("remarks"), element?.Element("value"));
            var raises = element?.Elements("exception")
                .Select(e => new RefRaise(Markdown.Unresolved((string?)e.Attribute("cref") ?? string.Empty), _markdown.Convert(e, type)))
                .ToList() ?? [];
            var deprecated = member.GetCustomAttribute<ObsoleteAttribute>() is { } obsolete ? obsolete.Message ?? "Deprecated." : null;
            switch (member)
            {
                case FieldInfo field when type.IsEnum:
                    return new RefMember(anchor, "value", name,
                        name + " = " + Convert.ToString(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture),
                        doc, [], null, raises, null, deprecated);
                case FieldInfo field:
                {
                    var words = field.IsLiteral ? "public const" : field.IsStatic ? (field.IsInitOnly ? "public static readonly" : "public static") : (field.IsInitOnly ? "public readonly" : "public");
                    var signature = words + " " + CSharp.TypeName(field.FieldType, _nullability.Create(field)) + " " + field.Name;
                    if (field.IsLiteral)
                    {
                        signature += " = " + CSharp.Literal(field.GetRawConstantValue(), field.FieldType, field.FieldType.IsValueType);
                    }
                    return new RefMember(anchor, "field", name, signature, doc, [], null, raises, null, deprecated);
                }
                case PropertyInfo property:
                    return new RefMember(anchor, "property", name, PropertySignature(type, property), doc,
                        Params(property.GetIndexParameters(), element, type), null, raises, null, deprecated);
                case EventInfo @event:
                    return new RefMember(anchor, "field", name,
                        "public event " + CSharp.TypeName(@event.EventHandlerType!) + " " + @event.Name, doc, [], null, raises, null, deprecated);
                case ConstructorInfo constructor:
                    return new RefMember(anchor, "constructor", name,
                        CSharp.MethodModifiers(constructor, false) + " " + CSharp.BareName(type) + CSharp.Parameters(constructor.GetParameters(), _nullability),
                        doc, Params(constructor.GetParameters(), element, type), null, raises, null, deprecated);
                case MethodInfo method:
                {
                    var inInterface = type.IsInterface;
                    var modifiers = CSharp.MethodModifiers(method, inInterface);
                    var returnType = CSharp.TypeName(method.ReturnType, _nullability.Create(method.ReturnParameter));
                    var generic = method.IsGenericMethodDefinition
                        ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">"
                        : string.Empty;
                    var signature = (modifiers.Length > 0 ? modifiers + " " : string.Empty) + returnType + " " + method.Name + generic
                        + CSharp.Parameters(method.GetParameters(), _nullability)
                        + (method.IsGenericMethodDefinition ? CSharp.Constraints(method.GetGenericArguments()) : string.Empty);
                    var returns = method.ReturnType == typeof(void) || method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask)
                        ? null
                        : new RefReturns(returnType, _markdown.Convert(element?.Element("returns"), type));
                    return new RefMember(anchor, "method", name, signature, doc, Params(method.GetParameters(), element, type),
                        returns, raises, Operation(type, method), deprecated);
                }
                default:
                    throw new InvalidOperationException($"no page entry for a {member.MemberType}");
            }
        }

        private string PropertySignature(Type type, PropertyInfo property)
        {
            var getter = property.GetMethod is { IsPublic: true } get ? get : null;
            var setter = property.SetMethod is { IsPublic: true } set ? set : null;
            var any = getter ?? setter!;
            var words = new List<string>();
            if (!type.IsInterface)
            {
                words.Add(CSharp.MethodModifiers(any, false));
            }
            if (property.IsDefined(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false))
            {
                words.Add("required");
            }
            var typeName = CSharp.TypeName(property.PropertyType, _nullability.Create(property));
            var indexes = property.GetIndexParameters();
            var name = indexes.Length > 0
                ? "this[" + string.Join(", ", indexes.Select(p => CSharp.Parameter(p, _nullability))) + "]"
                : property.Name;
            var accessors = new List<string>();
            if (getter is not null)
            {
                accessors.Add("get;");
            }
            if (setter is not null)
            {
                var init = setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                accessors.Add(init ? "init;" : "set;");
            }
            return string.Join(' ', words.Where(w => w.Length > 0).Append(typeName).Append(name)) + " { " + string.Join(' ', accessors) + " }";
        }

        private List<RefParam> Params(ParameterInfo[] parameters, XElement? element, Type type) =>
            parameters.Select(p => new RefParam(
                p.Name ?? string.Empty,
                CSharp.ParameterType(p, _nullability),
                CSharp.Default(p),
                _markdown.Convert(element?.Elements("param").FirstOrDefault(e => (string?)e.Attribute("name") == p.Name), type))).ToList();

        private string? Operation(Type type, MethodInfo method)
        {
            if (Operations.Fixed.TryGetValue(type.FullName + "." + method.Name, out var id))
            {
                return id;
            }
            if (method.IsStatic || !_resources.TryGetValue(type, out var resource))
            {
                return null;
            }
            if (Operations.FirstRequest(method, resource) is not { } sent)
            {
                return null;
            }
            return Operations.Match(_contract, sent.Method, sent.Path)?.OperationId
                ?? throw new InvalidOperationException($"{type.Name}.{method.Name} sends {sent.Method} {sent.Path}, which no operation of the contract takes");
        }

        /// <summary>The member's documentation element, following <c>inheritdoc</c> to what it inherits.</summary>
        private XElement? Documented(MemberInfo member)
        {
            for (var hops = 0; hops < 10; hops++)
            {
                var element = _xml.Element(DocXml.Id(member));
                if (element is null || element.Element("inheritdoc") is null)
                {
                    return element;
                }
                if (Inherited(member) is not { } next)
                {
                    return null;
                }
                member = next;
            }
            return null;
        }

        private static MemberInfo? Inherited(MemberInfo member)
        {
            switch (member)
            {
                case ConstructorInfo constructor:
                {
                    var types = constructor.GetParameters().Select(p => p.ParameterType).ToArray();
                    return constructor.DeclaringType!.BaseType?.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, types);
                }
                case MethodInfo method:
                {
                    var definition = method.GetBaseDefinition();
                    if (definition != method)
                    {
                        return definition;
                    }
                    foreach (var contract in method.DeclaringType!.GetInterfaces())
                    {
                        var map = method.DeclaringType.GetInterfaceMap(contract);
                        var at = Array.IndexOf(map.TargetMethods, method);
                        if (at >= 0)
                        {
                            return map.InterfaceMethods[at];
                        }
                    }
                    return null;
                }
                case PropertyInfo property:
                {
                    var accessor = property.GetMethod ?? property.SetMethod;
                    if (accessor is not null && Inherited(accessor) is MethodInfo baseAccessor)
                    {
                        return baseAccessor.DeclaringType!.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(p => p.GetMethod == baseAccessor || p.SetMethod == baseAccessor);
                    }
                    return null;
                }
                case Type type:
                    return type.BaseType;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// What the file must never contain (the format's rule 7), as the suite's checker reads it: work item and
    /// decision ids, MT5 as prose writes it, cell or cells, and an internal repository's name.
    /// </summary>
    [GeneratedRegex(@"(?i:AB#)|(?i:\bDEC-)|\bMT5\b|(?i:\bcells?\b)|(?i:-intern[a]l\b)")]
    public static partial Regex Internal();
}
