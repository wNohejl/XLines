using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data.CrossReference;
using XLines.Desk.Primitives;
using XLines.Web.Components.Desk;

namespace XLines.Web.Components.Panels;

/// <summary>
/// What the Board's cells say, as functions of the row alone: a side's short form, the Value
/// cell's pick and its sentences, the Score cell's state, marks and spoken score. Shared by the
/// panel and the cells it draws (BoardScoreCell, BoardValueCell); nothing here reads the panel.
/// </summary>
public static class BoardCells
{
    /// <summary>
    /// Which side a price column is for, as the team's own short form.
    ///
    /// <para>
    /// The board orders every market home-first so a column reads down consistently, but that
    /// ordering is only a convention until it is written on the cell. Abbreviations are right
    /// here — this is a dense grid where the same names repeat down the column, which is exactly
    /// where a short form belongs rather than where a game is being identified.
    /// </para>
    ///
    /// <para>
    /// Falls back to "H"/"A" for a team with no abbreviation on record, which is better than a
    /// blank: the reader still learns which side they are looking at.
    /// </para>
    /// </summary>
    public static string Side(BoardRow row, bool home)
    {
        var team = home ? row.Game.HomeTeam : row.Game.AwayTeam;

        return string.IsNullOrWhiteSpace(team?.Abbrev)
            ? home ? "H" : "A"
            : team!.Abbrev;
    }

    /// <summary>"LAC ML · FD +295" — the side, the market, and where to take it.</summary>
    public static string PickLabel(BoardRow row, ValuePick pick)
    {
        var first = pick.IsFirstSide(row.Game);
        var side = pick.Market == Markets.Total ? (first ? "O" : "U") : Side(row, first);
        var bet = pick.Market switch
        {
            Markets.Moneyline => $"{side} ML",
            _ when pick.Book.Line is { } line => pick.Market == Markets.Total
                ? $"{OddsMath.FormatLine(line, pick.Outcome)}"
                : $"{side} {OddsMath.FormatLine(line, pick.Outcome)}",
            _ => side
        };

        return $"{bet} · {PriceCell.Monogram(pick.Book.Book)} {OddsMath.FormatPrice(pick.Book.PriceAmerican)}";
    }

    /// <summary>
    /// The Value cell's tooltip, spoken: the side, the book by name (the feed's key, "draftkings",
    /// was printed here before), the price and the edge against the fair price.
    /// </summary>
    public static string ValueTitle(BoardRow row, ValuePick pick)
        => $"{pick.Outcome} at {BookNames.Display(pick.Book.Book)}, {OddsMath.FormatPrice(pick.Book.PriceAmerican)}: "
           + $"{PriceCell.EvLabel(pick.Book.Ev!.Value)} against the fair price — the most value on this game.";

    /// <summary>The same under its title: where to take it on one line, what it is worth on the next.</summary>
    public static string ValueCaption(ValuePick pick)
        => $"{pick.Outcome} at {BookNames.Display(pick.Book.Book)}, {OddsMath.FormatPrice(pick.Book.PriceAmerican)}\n"
           + $"{PriceCell.EvLabel(pick.Book.Ev!.Value)} against the fair price";

    public static DeskState StatusState(GameStatus status) => status switch
    {
        GameStatus.Live => DeskState.Warning,
        GameStatus.Final => DeskState.Positive,
        GameStatus.Postponed => DeskState.Negative,
        _ => DeskState.Info
    };

    /// <summary>
    /// A game whose state says it has been played but whose score has not arrived. That is a
    /// results pass that has not run, not an empty cell — so the row says which.
    /// </summary>
    public static string MissingScoreHint(GameStatus status)
        => status == GameStatus.Postponed ? "not played" : "no score yet";

    /// <summary>
    /// Who leads a finished game, so its loser can read lighter; Level or None for a game that
    /// is live, tied or unscored, where both sides keep full weight.
    /// </summary>
    public static ScoreLeader FinalLeader(Game game)
        => game.Status == GameStatus.Final ? Scoreline.Leader(game) : ScoreLeader.None;

    /// <summary>W, L or D for one side of a finished fight; NC for a no contest.</summary>
    public static string FightMark(Bout bout, ScoreLeader leader, bool home) => bout.Outcome switch
    {
        BoutOutcome.NoContest => "NC",
        BoutOutcome.Draw => "D",
        _ => leader == (home ? ScoreLeader.Home : ScoreLeader.Away) ? "W" : "L"
    };

    /// <summary>The score spelled out with full names: the cell's tooltip, and what it reads aloud.</summary>
    public static string ScoreTitle(Game game)
        => $"{game.AwayTeam?.Name ?? "Away"} {game.AwayScore ?? 0} — "
           + $"{game.HomeTeam?.Name ?? "Home"} {game.HomeScore ?? 0}";
}
