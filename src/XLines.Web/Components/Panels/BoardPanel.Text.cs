using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.Changes;
using XLines.Data.CrossReference;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Web.Components.Desk;
using XLines.Web.Components.Snippets;
using XLines.Web.Services;
using XLines.Web.Windowing;

namespace XLines.Web.Components.Panels;

public partial class BoardPanel
{
    private string HorizonNote => $"next {(_windowHours % 24 == 0 && _windowHours > 48 ? $"{_windowHours / 24}d" : $"{_windowHours}h")} · since midnight";

    /// <summary>
    /// Coverage in words, because "12 of 19" alone does not say whether the missing seven are a
    /// broken feed or seven games that have already been played. The books are a range when the
    /// rows differ: a slate where today's games carry ten books and next week's three read as
    /// "best of 10 books" before, which claimed ten for every row.
    /// </summary>
    private string CoverageNote
        => _rows.Count == 0 ? "nothing on the board"
            : _bookCount == 0 ? (_polled.Count > 0 ? $"none of {_polled.Count} polled books quoted" : "no book quoted this window")
            : _closing > 0
                ? $"best of {BooksPhrase}{PolledPhrase} · {_closing} closed"
                : $"best of {BooksPhrase}{PolledPhrase}";

    /// <summary>"Missing: Bally Bet (polled, not priced), Caesars (not polled)", or "all five priced".</summary>
    private string MainNote => _rows.Count == 0 ? "nothing on the board" : _main.Missing.Count == 0 ? "all five priced" : _main.ReasonsText;

    /// <summary>The state goes on the figure: all five priced is positive, a gap a warning.</summary>
    private DeskState MainState => _rows.Count == 0 ? DeskState.Neutral : _main.Missing.Count == 0 ? DeskState.Positive : DeskState.Warning;

    /// <summary>" · 10 polled" after the tile's book count, once the polled list is known.</summary>
    private string PolledPhrase => _polled.Count > 0 ? $" · {_polled.Count} polled" : "";

    /// <summary>
    /// Why a row has no price, from what the desk already holds: how many books are polled and
    /// when this league's lines were last pulled — " · 10 books polled · NBA lines last pulled
    /// Nov 14", or "never pulled". Nothing while the polled list is unknown, so the row reads
    /// "No price" as before.
    /// </summary>
    private string PolledWhy(BoardRow row) => UnpricedWhy(row, _polled);

    /// <summary>The words after "No price" on an unpriced row (<see cref="PolledWhy"/>).</summary>
    public static string UnpricedWhy(BoardRow row, IReadOnlyCollection<string>? polled)
    {
        if (polled is not { Count: > 0 })
            return "";

        var league = row.Game.Sport?.Key is { Length: > 0 } key ? key.ToUpperInvariant() + " lines" : "Lines";
        var pulled = row.LastPulled is { } at ? $"last pulled {DisplayTime.Day(at)}" : "never pulled";
        return $" · {polled.Count} {(polled.Count == 1 ? "book" : "books")} polled · {league} {pulled}";
    }

    /// <summary>"10 books", or "3–10 books" when the thinnest priced row has fewer than the fullest.</summary>
    private string BooksPhrase
        => _fewestBooks > 0 && _fewestBooks < _bookCount
            ? $"{_fewestBooks}–{_bookCount} books"
            : $"{_bookCount} {(_bookCount == 1 ? "book" : "books")}";

    private DeskState CoverageState
        => _rows.Count == 0 ? DeskState.Neutral
            : _unpriced == 0 ? DeskState.Positive
            : DeskState.Warning;

    /// <summary>
    /// "Quietest line", not "oldest". Prices are stored on change (ADR 0001), so an old
    /// timestamp means the line has not moved rather than that the data is stale — and a close
    /// contributes nothing here, because it is old by construction and will never move again.
    /// </summary>
    private string MovesNote
        => _oldest is { } quiet && quiet > TimeSpan.FromMinutes(1)
            ? $"quietest line {Age(quiet.TotalMinutes)} unmoved"
            : "written only when a price moves";

    /// <summary>
    /// Severity, not just a count. "2 open" says something is wrong; "1 critical" says whether
    /// it is worth interrupting what you were doing, which is the question being asked here.
    /// </summary>
    private string AlertNote => _alerts == 0
        ? "all clear"
        : _criticalAlerts > 0
            ? $"{_criticalAlerts} critical — open Ops"
            : "none critical — open Ops";

    private DeskState AlertState => _criticalAlerts > 0
        ? DeskState.Negative
        : _alerts > 0 ? DeskState.Warning : DeskState.Positive;

    private string PhaseWord => _phase switch
    {
        BoardPhase.Upcoming => "unstarted",
        BoardPhase.Live => "in-play",
        BoardPhase.Played => "finished",
        _ => ""
    };

    /// <summary>
    /// The row's classes: open, and a fight. A narrow Board stacks each market's two sides in one
    /// column level with the matchup's two lines (xlines.css), and a fight bills its red corner
    /// (home) first where a game reads away first, so the stack's order follows the row's kind.
    /// </summary>
    private string RowClass(BoardRow row)
        => string.Join(' ', new[]
        {
            _open?.Game.Id == row.Game.Id ? "board__row--open" : null,
            row.Bout is not null ? "board__row--fight" : null
        }.Where(c => c is not null));

    /// <summary>
    /// The side a total's cell names: nothing when the line already does ("o47.5" says Over), so
    /// the cell is a letter narrower; the letter when there is no line to say it.
    /// </summary>
    public static string? TotalSide(BestOffer? offer, string letter)
        => offer?.Line is null ? letter : null;

    /// <summary>The start's clock, "13:05 ET": the time half of <see cref="PanelBase.Starts"/>, the day being its own span.</summary>
    private static string StartClock(DateTimeOffset at)
        => $"{DisplayTime.Format(at, "HH:mm")} {DisplayTime.Label}";

    /// <summary>Which side a price column is for (<see cref="BoardCells.Side"/>).</summary>
    private static string Side(BoardRow row, bool home) => BoardCells.Side(row, home);

    /// <summary>
    /// The matchup link's tooltip: the game it opens, then each side's starter in full — the
    /// sentence the abbreviated "SP Gasser" stands for. The link takes focus, so this reaches
    /// the keyboard and a screen reader, where a title on the starter's own span did not.
    /// </summary>
    public static string MatchupTooltip(BoardRow row)
    {
        var game = row.Game;
        var fight = row.Bout is not null;
        var lines = new List<string> { WindowShortcuts.Describe(game) };

        // Billed order, as the cell draws it: red corner first for a fight, away first otherwise.
        foreach (var home in fight ? new[] { true, false } : new[] { false, true })
        {
            if (Starter(row, home) is { } s)
            {
                var team = home ? game.HomeTeam : game.AwayTeam;
                lines.Add($"{TeamNames.Short(team, Scoreline.Abbrev(team, home ? "Home" : "Away"))}: {s.Title}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// A club's starter as the matchup cell names it: the probable pitcher MLB has announced, or
    /// the quarterback an NFL club started its last game with. Null when the game has neither
    /// side's; a side not yet named reads TBD, so the two starters still line up under their clubs.
    /// </summary>
    public static (string Role, string Name, string Title)? Starter(BoardRow row, bool home)
    {
        var game = row.Game;

        // A fighter's record going into the bout stands where a starter would (ADR 0021).
        if (row.Bout is { } bout)
        {
            var record = home ? bout.HomeRecord : bout.AwayRecord;
            return record is null ? null : ("", record, $"Record going into this fight: {record}");
        }

        if (game.AwayProbablePitcher is not null || game.HomeProbablePitcher is not null)
        {
            var pitcher = home ? game.HomeProbablePitcher : game.AwayProbablePitcher;
            return ("SP", Surname(pitcher), pitcher is null
                ? "Starting pitcher not yet named"
                : $"Probable starting pitcher: {pitcher}");
        }

        if (row.AwayQuarterback is not null || row.HomeQuarterback is not null
            || row.AwayQuarterbackBasis is not null || row.HomeQuarterbackBasis is not null)
        {
            var quarterback = home ? row.HomeQuarterback : row.AwayQuarterback;
            var basis = home ? row.HomeQuarterbackBasis : row.AwayQuarterbackBasis;

            // A starter ruled out says so: the tooltip is the injury and who stands in. A plain
            // basis — the last game's starter, or this game's own passer once it is under way —
            // is said with the name after it.
            var plain = basis is XLines.Core.Analytics.Starters.LastGameBasis
                or XLines.Core.Analytics.Starters.ThisGameBasis;
            var title = basis is not null && !plain
                ? basis
                : quarterback is null
                    ? "No passing on record for this club"
                    : $"{basis ?? XLines.Core.Analytics.Starters.LastGameBasis}: {quarterback}";

            return ("QB", quarterback is null ? "—" : Surname(quarterback), title);
        }

        return null;
    }
}
