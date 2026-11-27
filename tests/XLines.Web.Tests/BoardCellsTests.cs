using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data.CrossReference;
using XLines.Desk.Primitives;
using XLines.Web.Components.Panels;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// What the Board's cells say, as functions of the row alone (BoardCells): the side's short form,
/// the Value cell's pick and its sentences, and the Score cell's state, marks and spoken score.
/// </summary>
public class BoardCellsTests
{
    private static ValuePick Pick(string market, string outcome, string book, int american, decimal? line = null, double ev = 0.027)
        => new(market, outcome, BoardRows.Rung(book, american, line, ev));

    [Fact]
    public void A_side_is_the_club_s_abbreviation_or_h_and_a_without_one()
    {
        var row = BoardRows.Row();
        Assert.Equal("KC", BoardCells.Side(row, home: true));
        Assert.Equal("BUF", BoardCells.Side(row, home: false));

        row.Game.HomeTeam!.Abbrev = " ";
        row.Game.AwayTeam = null;
        Assert.Equal("H", BoardCells.Side(row, home: true));
        Assert.Equal("A", BoardCells.Side(row, home: false));
    }

    [Fact]
    public void The_row_s_best_pick_reads_as_the_side_the_market_and_where_to_take_it()
    {
        var row = BoardRows.Row();

        Assert.Equal("KC ML · DK -128", BoardCells.PickLabel(row, row.BestPick!));
        Assert.Equal("BUF +2.5 · FD -105", BoardCells.PickLabel(row, Pick(Markets.Spread, BoardRows.Away, "fanduel", -105, 2.5m)));

        // A total's line already names its side, so the pick is the line alone.
        Assert.Equal("o47.5 · DK -110", BoardCells.PickLabel(row, Pick(Markets.Total, "Over", "draftkings", -110, 47.5m)));
        Assert.Equal("U · FD +105", BoardCells.PickLabel(row, Pick(Markets.Total, "Under", "fanduel", 105)));
    }

    [Fact]
    public void The_value_cell_s_tooltip_names_the_book_the_price_and_the_edge()
    {
        var row = BoardRows.Row();
        var pick = row.BestPick!;

        Assert.Equal("Kansas City Chiefs at DraftKings, -128: +2.7% EV against the fair price — the most value on this game.",
            BoardCells.ValueTitle(row, pick));
        Assert.Equal("Kansas City Chiefs at DraftKings, -128\n+2.7% EV against the fair price", BoardCells.ValueCaption(pick));
    }

    [Theory]
    [InlineData(GameStatus.Live, DeskState.Warning)]
    [InlineData(GameStatus.Final, DeskState.Positive)]
    [InlineData(GameStatus.Postponed, DeskState.Negative)]
    [InlineData(GameStatus.Scheduled, DeskState.Info)]
    public void A_game_s_state_takes_its_tag_s_colour(GameStatus status, DeskState expected)
        => Assert.Equal(expected, BoardCells.StatusState(status));

    [Fact]
    public void A_missing_score_says_whether_the_game_was_played()
    {
        Assert.Equal("not played", BoardCells.MissingScoreHint(GameStatus.Postponed));
        Assert.Equal("no score yet", BoardCells.MissingScoreHint(GameStatus.Final));
    }

    [Fact]
    public void Only_a_final_has_a_leader()
    {
        var game = BoardRows.Game();
        game.HomeScore = 24;
        game.AwayScore = 17;

        game.Status = GameStatus.Live;
        Assert.Equal(ScoreLeader.None, BoardCells.FinalLeader(game));

        game.Status = GameStatus.Final;
        Assert.Equal(ScoreLeader.Home, BoardCells.FinalLeader(game));

        game.HomeScore = 17;
        Assert.Equal(ScoreLeader.Level, BoardCells.FinalLeader(game));
    }

    [Fact]
    public void A_fight_s_marks_are_w_and_l_d_for_a_draw_and_nc_for_no_contest()
    {
        var win = new Bout { Outcome = BoutOutcome.Win };
        Assert.Equal("W", BoardCells.FightMark(win, ScoreLeader.Home, home: true));
        Assert.Equal("L", BoardCells.FightMark(win, ScoreLeader.Home, home: false));
        Assert.Equal("W", BoardCells.FightMark(win, ScoreLeader.Away, home: false));

        Assert.Equal("D", BoardCells.FightMark(new Bout { Outcome = BoutOutcome.Draw }, ScoreLeader.Level, home: true));
        Assert.Equal("NC", BoardCells.FightMark(new Bout { Outcome = BoutOutcome.NoContest }, ScoreLeader.None, home: false));
    }

    [Fact]
    public void The_score_is_spoken_away_first_with_full_names()
    {
        var game = BoardRows.Game();
        game.AwayScore = 17;
        Assert.Equal("Buffalo Bills 17 — Kansas City Chiefs 0", BoardCells.ScoreTitle(game));

        game.HomeTeam = null;
        Assert.Equal("Buffalo Bills 17 — Home 0", BoardCells.ScoreTitle(game));
    }
}
