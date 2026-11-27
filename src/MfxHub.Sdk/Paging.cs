using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization;

namespace MfxHub;

/// <summary>
/// One page of a list, and the cursor for the next. Every list in the API is cursor-paginated and has
/// no total, no page number and no offset, so a page never says how many there are in all.
/// </summary>
/// <typeparam name="T">What the list holds.</typeparam>
/// <remarks>
/// Enumerate it, or read <see cref="Items"/>. To walk every page, pass <see cref="NextCursor"/> back
/// as the cursor, or use the method that does it for you (<see cref="Terminals.ListAsync"/>).
/// </remarks>
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix",
    Justification = "Page<T> is the name every MFX SDK gives one page; it is not a collection type")]
public sealed class Page<T> : IReadOnlyList<T>
{
    /// <summary>A page of <paramref name="items"/>.</summary>
    /// <param name="items">What is in this page.</param>
    /// <param name="nextCursor">The cursor for the next page; null or empty on the last, which reads as null.</param>
    /// <param name="returned">How many are in this page, as the platform said; null when it did not say.</param>
    /// <param name="source">Where the rows came from, on the lists that say (<c>live</c>, <c>last_known</c>).</param>
    public Page(IEnumerable<T> items, string? nextCursor = null, int? returned = null, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = [.. items];
        // An empty cursor would otherwise read as a cursor and fetch the first page again for ever.
        NextCursor = string.IsNullOrEmpty(nextCursor) ? null : nextCursor;
        Returned = returned;
        Source = string.IsNullOrEmpty(source) ? null : source;
    }

    /// <summary>What is in this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>The cursor for the next page; null on the last.</summary>
    public string? NextCursor { get; }

    /// <summary>How many are in THIS page, as the platform said. Not a total, and never to be shown as one.</summary>
    public int? Returned { get; }

    /// <summary>Where the rows came from, on the lists that say; null on every other.</summary>
    public string? Source { get; }

    /// <summary>How many are in this page.</summary>
    public int Count => Items.Count;

    /// <summary>The item at <paramref name="index"/> in this page.</summary>
    /// <param name="index">Its place in this page, from 0.</param>
    public T this[int index] => Items[index];

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>How a filter's values go on the wire.</summary>
internal static class WireNames
{
    /// <summary>
    /// The contract's name for <paramref name="value"/>: the generated enum's
    /// <see cref="JsonStringEnumMemberNameAttribute"/>. A value the enum does not define is refused
    /// here, so a bad one is the caller's error and not a 400 from the platform; so is a generated
    /// enum's <c>UnknownValue</c>, which has no word to send.
    /// </summary>
    public static string Of<TEnum>(TEnum value, string parameter)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameter, value, $"not a {typeof(TEnum).Name}");
        }
        if (UnknownEnumValue.Is(value))
        {
            throw new ArgumentOutOfRangeException(parameter, value, UnknownEnumValue.Refusal(typeof(TEnum)));
        }
        var name = value.ToString();
        return typeof(TEnum).GetField(name)?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? name;
    }

    /// <summary>
    /// A filter of one value or several, as one comma-separated value, as the contract reads it. Nothing
    /// (null or none) is left out of the request, so it is null then.
    /// </summary>
    public static string? Joined<TEnum>(IEnumerable<TEnum>? values, string parameter)
        where TEnum : struct, Enum
    {
        if (values is null)
        {
            return null;
        }
        var names = values.Select(v => Of(v, parameter)).ToList();
        return names.Count == 0 ? null : string.Join(',', names);
    }
}
