using XLines.Core.Entities;

namespace XLines.Data.CrossReference;

/// <summary>
/// Which closes a game is read from: the book market's where there is one, the stats provider's
/// reference close only where the market never saw the game (ADR 0011).
///
/// <para>
/// The stats provider records a single-book reference close for every final under its own
/// source. It is never mixed into a market: a reference is one book, and letting it into a
/// consensus or a "best" drawn from several would move the number it is meant to be compared
/// against. The rule is per game, not global, so a fixture the odds feed missed falls back to the
/// reference instead of to nothing. The board and the game log both read closes through here.
/// </para>
/// </summary>
internal static class MarketFirstClose
{
    /// <summary>The closes per game that the game is read from, keyed by game.</summary>
    public static IEnumerable<KeyValuePair<int, IEnumerable<T>>> PerGame<T>(
        IEnumerable<T> closes, Func<T, int> gameId, Func<T, SourceKind> kind)
        => closes
            .GroupBy(gameId)
            .Select(g => new KeyValuePair<int, IEnumerable<T>>(
                g.Key,
                g.Any(c => kind(c) == SourceKind.Odds) ? g.Where(c => kind(c) == SourceKind.Odds) : g));
}
