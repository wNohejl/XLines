using System.Globalization;
using XLines.Data.CrossReference;

namespace XLines.Web.Components.Snippets;

/// <summary>One side's leader in one category: who, the figure that leads, and the quiet line under it.</summary>
public sealed record GameLeader(int PlayerId, string Name, string Value, string? Line);

/// <summary>One category's row: its name and each side's leader, either of which may be missing.</summary>
public sealed record GameLeadersRow(string Label, GameLeader? Away, GameLeader? Home);

/// <summary>
/// ESPN's "Game leaders", read from the box score the Gamecast already holds: passing, rushing and
/// receiving yards, then sacks and tackles, each side's top line in each.
///
/// <para>
/// The keys are the ones <see cref="GameBoxScore"/> hands out after <see cref="StatLineKeys"/> has
/// qualified them, so a label two ESPN groups print arrives under its group (<c>passing.YDS</c>,
/// <c>rushing.YDS</c>, <c>receiving.YDS</c>, <c>receiving.REC</c>, <c>passing.TD</c>,
/// <c>defensive.SACKS</c>) and one a single group prints stays bare (<c>C/ATT</c>, <c>CAR</c>,
/// <c>TOT</c>, <c>SOLO</c>). That matters most for sacks: a passer's line carries
/// <c>passing.SACKS</c> as "3-19", the sacks he took, and only <c>defensive.SACKS</c> is a sack
/// made. The stored NFL box scores carry the defensive group (<c>TOT</c>, <c>SOLO</c>,
/// <c>defensive.SACKS</c>, <c>TFL</c>) for every defender, so sacks and tackles are drawn as
/// ESPN's are, after the three yardage rows.
/// </para>
///
/// <para>
/// A leader is the line with the most of the category's figure; a tie keeps the first line the box
/// lists, as ESPN's order is. Yards lead whatever they are (a side whose only rusher lost a yard
/// still has a rushing leader); a count of sacks or tackles leads only above zero, since "0 sacks"
/// names no one. A figure that does not read as a number is passed over rather than guessed at.
/// A side with no leader is null (the row draws a dash); a category neither side leads is left out;
/// and an empty box, or a league whose box is not football's, has no rows at all.
/// </para>
/// </summary>
public static class GameLeadersModel
{
    private sealed record Category(string Label, string Key, bool PositiveOnly, Func<IReadOnlyDictionary<string, string>, string?> Line);

    private static readonly Category[] Categories =
    [
        new("Passing yards", "passing.YDS", false, s => Join(Raw(s, "C/ATT"), Count(s, "passing.TD", "TD"), Count(s, "passing.INT", "INT"))),
        new("Rushing yards", "rushing.YDS", false, s => Join(Count(s, "CAR", "car", always: true), Count(s, "rushing.TD", "TD"))),
        new("Receiving yards", "receiving.YDS", false, s => Join(Count(s, "receiving.REC", "rec", always: true), Count(s, "receiving.TD", "TD"))),
        new("Sacks", "defensive.SACKS", true, s => Join(Count(s, "TOT", "tkl", always: true), Count(s, "TFL", "TFL"))),
        new("Tackles", "TOT", true, s => Join(Count(s, "SOLO", "solo", always: true), Sacks(s))),
    ];

    /// <summary>The rows to draw, in ESPN's order; none for an empty or missing box, or a league that is not football.</summary>
    public static IReadOnlyList<GameLeadersRow> For(GameBoxScore? box, string? sport = null)
    {
        if (box is null || box.IsEmpty || sport is not (null or "nfl" or "ncaaf"))
            return [];

        var rows = new List<GameLeadersRow>();

        foreach (var category in Categories)
        {
            var away = Leader(box.Away, category);
            var home = Leader(box.Home, category);

            if (away is not null || home is not null)
                rows.Add(new GameLeadersRow(category.Label, away, home));
        }

        return rows;
    }

    private static GameLeader? Leader(IReadOnlyList<BoxScoreLine> side, Category category)
    {
        BoxScoreLine? best = null;
        var bestValue = double.NegativeInfinity;

        foreach (var line in side)
        {
            if (Number(line.Stats, category.Key) is not { } value || category.PositiveOnly && value <= 0)
                continue;

            // Strictly more, so a tie keeps the first line the box lists.
            if (value > bestValue)
            {
                best = line;
                bestValue = value;
            }
        }

        return best is null
            ? null
            : new GameLeader(best.PlayerId, best.Name, best.Stats[category.Key].Trim(), category.Line(best.Stats));
    }

    private static double? Number(IReadOnlyDictionary<string, string> stats, string key)
        => stats.TryGetValue(key, out var raw)
           && double.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string? Raw(IReadOnlyDictionary<string, string> stats, string key)
        => stats.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;

    /// <summary>"2 TD"; nothing for a zero unless <paramref name="always"/>, so the line names what happened and not what did not.</summary>
    private static string? Count(IReadOnlyDictionary<string, string> stats, string key, string unit, bool always = false)
        => Number(stats, key) is { } n && (always || n > 0) ? $"{Raw(stats, key)} {unit}" : null;

    private static string? Sacks(IReadOnlyDictionary<string, string> stats)
        => Number(stats, "defensive.SACKS") is { } n && n > 0
            ? $"{Raw(stats, "defensive.SACKS")} {(n == 1 ? "sack" : "sacks")}"
            : null;

    private static string? Join(params string?[] parts)
    {
        var said = parts.Where(p => p is not null).ToArray();
        return said.Length == 0 ? null : string.Join(", ", said);
    }
}
