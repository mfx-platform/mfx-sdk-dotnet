using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace MfxHub.SdkRef;

/// <summary>Types and members written as C# writes them, nullability included, on one line.</summary>
internal static class CSharp
{
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void",
        [typeof(object)] = "object",
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(char)] = "char",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(nint)] = "nint",
        [typeof(nuint)] = "nuint",
    };

    /// <summary>A type's name without its generic arity: <c>Page</c> for <c>Page`1</c>.</summary>
    public static string BareName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>A type's declared name with its type parameters: <c>Page&lt;T&gt;</c>.</summary>
    public static string DeclaredName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsGenericTypeDefinition
            ? BareName(type) + "<" + string.Join(", ", type.GetGenericArguments().Select(a => a.Name)) + ">"
            : BareName(type);
    }

    /// <summary>
    /// <paramref name="type"/> as C# writes it, with the nullability <paramref name="nullability"/> describes; a
    /// reference type is read as not nullable when nothing describes it.
    /// </summary>
    public static string TypeName(Type type, NullabilityInfo? nullability = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsByRef)
        {
            return TypeName(type.GetElementType()!, nullability?.ElementType ?? nullability);
        }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return TypeName(underlying, nullability?.GenericTypeArguments.FirstOrDefault()) + "?";
        }
        var mark = nullability is { ReadState: NullabilityState.Nullable } && !type.IsValueType ? "?" : string.Empty;
        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            return TypeName(type.GetElementType()!, nullability?.ElementType) + "[" + new string(',', rank - 1) + "]" + mark;
        }
        if (type.IsGenericParameter)
        {
            return type.Name + mark;
        }
        if (Keywords.TryGetValue(type, out var keyword))
        {
            return keyword + mark;
        }
        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            var infos = nullability?.GenericTypeArguments;
            var names = arguments.Select((a, i) => TypeName(a, infos is { Length: > 0 } && i < infos.Length ? infos[i] : null)).ToArray();
            if (type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
            {
                return "(" + string.Join(", ", names) + ")" + mark;
            }
            return BareName(type) + "<" + string.Join(", ", names) + ">" + mark;
        }
        return type.Name + mark;
    }

    /// <summary><paramref name="type"/> as C# writes it, with the nullability the compiler recorded in <paramref name="flags"/>.</summary>
    public static string TypeName(Type type, NullableFlags flags)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(flags);
        if (type.IsByRef || type.IsPointer)
        {
            return TypeName(type.GetElementType()!, flags) + (type.IsPointer ? "*" : string.Empty);
        }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return TypeName(underlying, flags) + "?";
        }
        if (type.IsArray)
        {
            var mark = flags.Next() == 2 ? "?" : string.Empty;
            return TypeName(type.GetElementType()!, flags) + "[" + new string(',', type.GetArrayRank() - 1) + "]" + mark;
        }
        if (type.IsGenericParameter)
        {
            return type.Name + (flags.Next() == 2 ? "?" : string.Empty);
        }
        if (type.IsValueType && !type.IsGenericType)
        {
            return TypeName(type);
        }
        var own = flags.Next();
        var self = !type.IsValueType && own == 2 ? "?" : string.Empty;
        if (!type.IsGenericType)
        {
            return TypeName(type) + self;
        }
        var names = type.GetGenericArguments().Select(a => TypeName(a, flags)).ToArray();
        if (type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
        {
            return "(" + string.Join(", ", names) + ")" + self;
        }
        return BareName(type) + "<" + string.Join(", ", names) + ">" + self;
    }

    /// <summary>The access and other modifiers C# writes before a method.</summary>
    public static string MethodModifiers(MethodBase method, bool inInterface)
    {
        ArgumentNullException.ThrowIfNull(method);
        var words = new List<string>();
        if (!inInterface)
        {
            words.Add(method.IsPublic ? "public" : "protected");
        }
        if (method.IsStatic)
        {
            words.Add("static");
        }
        else if (method is MethodInfo info && !inInterface)
        {
            var overrides = info.GetBaseDefinition().DeclaringType != info.DeclaringType;
            if (overrides)
            {
                words.Add(info.IsFinal ? "sealed override" : "override");
            }
            else if (info.IsAbstract)
            {
                words.Add("abstract");
            }
            else if (info.IsVirtual && !info.IsFinal)
            {
                words.Add("virtual");
            }
        }
        return string.Join(' ', words);
    }

    /// <summary>A parameter list as C# writes it: <c>(Guid terminalId, string? comment = null)</c>.</summary>
    public static string Parameters(IEnumerable<ParameterInfo> parameters, Nullability context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return "(" + string.Join(", ", parameters.Select(p => Parameter(p, context))) + ")";
    }

    /// <summary>One parameter as C# writes it.</summary>
    public static string Parameter(ParameterInfo parameter, Nullability context)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(context);
        var text = new StringBuilder();
        if (parameter.IsDefined(typeof(ParamArrayAttribute), false) || parameter.IsDefined(typeof(ParamCollectionAttribute), false))
        {
            text.Append("params ");
        }
        if (parameter.ParameterType.IsByRef)
        {
            text.Append(parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ");
        }
        text.Append(ParameterType(parameter, context)).Append(' ').Append(parameter.Name);
        if (Default(parameter) is { } value)
        {
            text.Append(" = ").Append(value);
        }
        return text.ToString();
    }

    /// <summary>A parameter's type as C# writes it.</summary>
    public static string ParameterType(ParameterInfo parameter, Nullability context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TypeName(parameter.ParameterType, context.Create(parameter));
    }

    /// <summary>A parameter's default as C# writes it; null when the parameter is required.</summary>
    public static string? Default(ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!parameter.HasDefaultValue)
        {
            return null;
        }
        var type = parameter.ParameterType;
        return Literal(parameter.DefaultValue, Nullable.GetUnderlyingType(type) ?? type, type.IsValueType && Nullable.GetUnderlyingType(type) is null);
    }

    /// <summary>A constant as C# writes it.</summary>
    public static string Literal(object? value, Type type, bool valueType)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (value)
        {
            case null:
                return valueType ? "default" : "null";
            case string text:
                return Quoted(text);
            case bool flag:
                return flag ? "true" : "false";
            case char ch:
                return "'" + (ch == '\'' ? "\\'" : ch.ToString()) + "'";
            case decimal number:
                return number.ToString(CultureInfo.InvariantCulture) + "m";
            case float number:
                return number.ToString("R", CultureInfo.InvariantCulture) + "f";
            case double number:
                return number.ToString("R", CultureInfo.InvariantCulture);
        }
        if (type.IsEnum)
        {
            var named = Enum.ToObject(type, value);
            var name = Enum.GetName(type, named);
            return name is null
                ? "(" + TypeName(type) + ")" + Convert.ToString(value, CultureInfo.InvariantCulture)
                : TypeName(type) + "." + name;
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default";
    }

    private static string Quoted(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (var ch in text)
        {
            quoted.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString(),
            });
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>A generic method's or type's constraints, as <c>where</c> clauses; empty when there are none.</summary>
    public static string Constraints(IEnumerable<Type> parameters)
    {
        var clauses = new List<string>();
        foreach (var parameter in parameters)
        {
            var parts = new List<string>();
            var flags = parameter.GenericParameterAttributes;
            if (flags.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                parts.Add("class");
            }
            if (flags.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("struct");
            }
            parts.AddRange(parameter.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(c => TypeName(c)));
            if (flags.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)
                && !flags.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("new()");
            }
            if (parts.Count > 0)
            {
                clauses.Add(" where " + parameter.Name + " : " + string.Join(", ", parts));
            }
        }
        return string.Concat(clauses);
    }

    /// <summary>Whether the compiler, not the source, wrote <paramref name="member"/>.</summary>
    public static bool CompilerWritten(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.IsDefined(typeof(CompilerGeneratedAttribute), false) || member.Name.Contains('<', StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="type"/> is a record (a class with the compiler's clone method).</summary>
    public static bool IsRecord(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsClass && type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(m => m.Name == "<Clone>$");
    }
}

/// <summary>
/// Nullability as the source wrote it, read from the compiler's own metadata: the <c>NullableAttribute</c> on the
/// member, parameter or return, or else the nearest <c>NullableContextAttribute</c>. Reflection's
/// <c>NullabilityInfoContext</c> reports every unconstrained <c>T</c> as nullable, so it cannot tell <c>T</c> from
/// <c>T?</c>; the metadata can.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "an instance the signature writers are handed, as they were handed reflection's context")]
internal sealed class Nullability
{
    private const string Attribute = "System.Runtime.CompilerServices.NullableAttribute";
    private const string Context = "System.Runtime.CompilerServices.NullableContextAttribute";

    public NullableFlags Create(ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return Own(parameter.CustomAttributes) ?? FromContext(parameter.Member);
    }

    public NullableFlags Create(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Own(property.CustomAttributes) ?? FromContext((MemberInfo?)property.GetMethod ?? property.DeclaringType);
    }

    public NullableFlags Create(FieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Own(field.CustomAttributes) ?? FromContext(field.DeclaringType);
    }

    private static NullableFlags? Own(IEnumerable<CustomAttributeData> attributes)
    {
        var found = attributes.FirstOrDefault(a => a.AttributeType.FullName == Attribute);
        if (found is null)
        {
            return null;
        }
        var value = found.ConstructorArguments[0].Value;
        return value switch
        {
            byte one => new NullableFlags([one]),
            IReadOnlyCollection<CustomAttributeTypedArgument> many => new NullableFlags(many.Select(a => (byte)a.Value!).ToArray()),
            _ => new NullableFlags([0]),
        };
    }

    private static NullableFlags FromContext(MemberInfo? member)
    {
        for (var at = member; at is not null; at = at.DeclaringType)
        {
            var context = at.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == Context);
            if (context is not null)
            {
                return new NullableFlags([(byte)context.ConstructorArguments[0].Value!]);
            }
        }
        return new NullableFlags([0]);
    }
}

/// <summary>
/// The compiler's nullability bytes for one type, read in its order (pre-order: a reference type, array or type
/// parameter takes one byte, 2 meaning annotated; a generic value type takes one it ignores; any other value type
/// none). A single byte stands for every position.
/// </summary>
internal sealed class NullableFlags(byte[] bytes)
{
    private int _at;

    public byte Next() => bytes.Length == 1 ? bytes[0] : _at < bytes.Length ? bytes[_at++] : (byte)0;
}
