using System.Globalization;
using System.Text.Json;

namespace MfxHub;

/// <summary>
/// One terminal's account as the stream last said it was: a snapshot, with the marks applied.
/// </summary>
/// <param name="TerminalId">The terminal.</param>
/// <param name="AsOf">The <c>as_of</c> of the last message applied, not of the snapshot: the figures are that fresh.</param>
/// <param name="Seq">The <c>seq</c> of the last message applied.</param>
/// <param name="Base">The <c>seq</c> of the snapshot this view is drawn from, which every marks message names.</param>
/// <param name="Figures">Every figure by the name it arrived under, one the platform adds later included.</param>
/// <param name="Positions">The open positions by ticket, in the snapshot's own order.</param>
/// <param name="Pending">The pending orders, as the snapshot listed them.</param>
public sealed record AccountView(
    string TerminalId,
    string? AsOf,
    long? Seq,
    long? Base,
    IReadOnlyDictionary<string, JsonElement> Figures,
    IReadOnlyDictionary<long, IReadOnlyDictionary<string, JsonElement>> Positions,
    IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Pending);

/// <summary>
/// Follows one terminal's account live on <c>account:{terminal_id}</c>, snapshot then marks, by the
/// stream's ordering rules. It holds no socket: the caller drives the stream and hands it every event.
/// </summary>
/// <remarks>
/// <code>
/// var live = new LiveAccount(terminalId);
/// await foreach (var e in stream)
/// {
///     if (live.Apply(e) is { } view) Show(view);
///     else if (live.Behind) await stream.ResumeAsync(Channel.Account(terminalId));
/// }
/// </code>
/// It never guesses: a message it cannot apply is dropped whole and <see cref="Behind"/> goes true; it
/// does not half-apply, and it does not patch a position it has never seen. Only a snapshot clears
/// <see cref="Behind"/>; one arrives within 60 s on its own, or at once on a resume. A mark is
/// positional, <c>[ticket, price_current, profit, swap]</c>, and is read by position.
/// </remarks>
public sealed class LiveAccount
{
    /// <summary>The message that carries the whole account.</summary>
    public const string Snapshot = "account.snapshot";

    /// <summary>The message that carries only the positions whose floating values moved.</summary>
    public const string Marks = "account.marks";

    /// <summary>Carried by a message but not a figure: protocol, a field of the view, or the delta itself.</summary>
    private static readonly HashSet<string> NotAFigure =
        new(["kind", "type", "seq", "base", "as_of", "terminal_id", "schema_version", "marks"], StringComparer.Ordinal);

    /// <summary>A snapshot's fields that belong to the position set: only another snapshot replaces them.</summary>
    private static readonly HashSet<string> Set =
        new(["positions", "pending", "pending_as_of", "open_positions_count"], StringComparer.Ordinal);

    /// <summary>What a mark sets, in the order <c>AccountMarksMessage.marks[]</c> carries it after the ticket.</summary>
    private static readonly string[] Marked = ["price_current", "profit", "swap"];

    private readonly string _terminalId;

    /// <summary>A view of <paramref name="terminalId"/>'s account, empty until its first snapshot.</summary>
    /// <param name="terminalId">The terminal.</param>
    public LiveAccount(Guid terminalId) => _terminalId = Wire.Id(terminalId);

    /// <summary>
    /// The account, or null until the first snapshot arrives: an account with no positions and a zero
    /// balance is a statement about a tenant's money, and "not told yet" is not that statement.
    /// </summary>
    public AccountView? View { get; private set; }

    /// <summary>True when a message could not be applied, so this view is not the account. The remedy is a resume.</summary>
    public bool Behind { get; private set; }

    /// <summary>
    /// Takes one stream event. Returns the new view, or null when nothing was applied: an event for
    /// another channel, terminal or kind; a marks message that does not apply; a resync; a gap.
    /// </summary>
    /// <param name="streamEvent">The event, as the stream delivered it.</param>
    /// <returns>The new view, or null.</returns>
    public AccountView? Apply(StreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        switch (streamEvent)
        {
            case ChannelRefused or Resync:
                if (!Mine(streamEvent.Channel, default))
                {
                    return null;
                }
                // The channel is gone, or the replay window has passed: what is held may be
                // arbitrarily old, and only a snapshot can correct it.
                (View, Behind) = (null, true);
                return null;
            case StreamMessage message:
                if (!Mine(message.Channel, message.Data.GetValueOrDefault("terminal_id")))
                {
                    return null;
                }
                var kind = message.Type ?? Text(message.Data.GetValueOrDefault("kind")) ?? string.Empty;
                return kind switch
                {
                    Snapshot => Draw(message),
                    Marks => Mark(message),
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// Is this event about this terminal? A message that names a different terminal is not ours whatever
    /// the channel said, so a mark can never be applied to another account's positions.
    /// </summary>
    private bool Mine(string channel, JsonElement terminalId)
    {
        if (terminalId.ValueKind != JsonValueKind.Undefined && terminalId.ValueKind != JsonValueKind.Null
            && !string.Equals(Text(terminalId), _terminalId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return string.IsNullOrEmpty(channel) || channel.EndsWith(_terminalId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces the whole view with every snapshot, not only a newer one: the channel's seq restarts after
    /// a quiet terminal. <c>base</c> is taken from the message, never inferred from <c>seq</c>, because a
    /// snapshot served on a resume already includes the marks up to its seq.
    /// </summary>
    private AccountView Draw(StreamMessage message)
    {
        var data = message.Data;
        Behind = false;
        var positions = new OrderedDictionary<long, IReadOnlyDictionary<string, JsonElement>>();
        foreach (var row in Rows(data.GetValueOrDefault("positions")))
        {
            if (row.TryGetValue("ticket", out var ticket))
            {
                positions[Integer(ticket)] = row;
            }
        }
        View = new AccountView(
            _terminalId,
            Text(data.GetValueOrDefault("as_of")),
            message.Seq,
            BaseOf(message) ?? message.Seq,
            Figures(data),
            positions,
            Rows(data.GetValueOrDefault("pending")));
        return View;
    }

    /// <summary>
    /// Applies a marks message, or drops it whole and falls behind: nothing held, a base that is not the
    /// snapshot held, a gap, a ticket not held, or a mark that is not the four-element array the contract
    /// defines. An empty list is a keep-alive and applies; an unreadable mark is not an empty one.
    /// </summary>
    private AccountView? Mark(StreamMessage message)
    {
        var view = View;
        if (view is null)
        {
            Behind = true;
            return null;
        }
        var @base = BaseOf(message);
        var marks = MarksOf(message.Data.GetValueOrDefault("marks"));
        if ((@base is not null && @base != view.Base) || Gapped(view.Seq, message.Seq) || marks is null
            || marks.Any(m => !view.Positions.ContainsKey(m.Ticket)))
        {
            Behind = true;
            return null;
        }
        var positions = new OrderedDictionary<long, IReadOnlyDictionary<string, JsonElement>>();
        foreach (var (ticket, row) in view.Positions)
        {
            positions[ticket] = row;
        }
        foreach (var (ticket, values) in marks)
        {
            var row = new Dictionary<string, JsonElement>(positions[ticket], StringComparer.Ordinal);
            for (var i = 0; i < Marked.Length; i++)
            {
                row[Marked[i]] = values[i];
            }
            positions[ticket] = row;
        }
        var figures = new Dictionary<string, JsonElement>(view.Figures, StringComparer.Ordinal);
        foreach (var (name, value) in Figures(message.Data))
        {
            figures[name] = value;
        }
        View = new AccountView(
            view.TerminalId,
            Text(message.Data.GetValueOrDefault("as_of")) ?? view.AsOf,
            message.Seq ?? view.Seq,
            view.Base,
            figures,
            positions,
            view.Pending);
        return View;
    }

    /// <summary>The marks as (ticket, [price_current, profit, swap]), or null when one is not the contract's shape.</summary>
    private static List<(long Ticket, JsonElement[] Values)>? MarksOf(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var marks = new List<(long, JsonElement[])>();
        foreach (var mark in value.EnumerateArray())
        {
            if (mark.ValueKind != JsonValueKind.Array || mark.GetArrayLength() != 1 + Marked.Length)
            {
                return null;
            }
            var items = mark.EnumerateArray().ToArray();
            var ticket = Integer(items[0]);
            if (ticket < 0)
            {
                return null;
            }
            marks.Add((ticket, items[1..]));
        }
        return marks;
    }

    /// <summary>The seq of the snapshot a message applies to, from the envelope or the payload; null when the platform did not say.</summary>
    private static long? BaseOf(StreamMessage message)
    {
        if (message.Base is { } envelope)
        {
            return envelope;
        }
        return message.Data.TryGetValue("base", out var inData) && inData.ValueKind != JsonValueKind.Null ? Integer(inData) : null;
    }

    private static Dictionary<string, JsonElement> Figures(IReadOnlyDictionary<string, JsonElement> data) =>
        data.Where(p => !NotAFigure.Contains(p.Key) && !Set.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static List<IReadOnlyDictionary<string, JsonElement>> Rows(JsonElement value) =>
        value.ValueKind != JsonValueKind.Array
            ? []
            : [.. value.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object)
                .Select(r => (IReadOnlyDictionary<string, JsonElement>)r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal))];

    /// <summary>Unknown on either side is not a gap: a stream that numbers nothing cannot prove a loss.</summary>
    private static bool Gapped(long? held, long? arriving) => held is not null && arriving is not null && arriving > held + 1;

    private static long Integer(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number,
        _ => -1,
    };

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}
