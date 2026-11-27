using Bunit;
using XLines.Data.Gamecast;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The play log's headline: a football play's row leads its body with the headline, then the down
/// and the sentence; a play with nothing worth heading, and another league's play, draw none; and
/// past the virtualisation threshold the rows are headed the same.
/// </summary>
public class PlayLogTests : DeskTestContext
{
    private static GamecastPlay Pass(long sequence) => FakeGamecastData.Play(sequence, 1,
        "(Shotgun) B.Young pass deep middle to X.Legette to ATL 10 for 55 yards (M.Hughes).") with
    {
        Type = "Pass Reception",
        Yardage = 55,
        Start = new GamecastSituation(1, 10, 35, 65, "1st & 10 at CAR 35", "CAR 35", FakeGamecastData.AwayId),
    };

    private IRenderedComponent<PlayLog> Render(IReadOnlyList<GamecastPlay> plays, string? sport = "nfl", int virtualizeAt = 150)
        => RenderComponent<PlayLog>(p => p
            .Add(l => l.Plays, plays.ToList()).Add(l => l.Sport, sport).Add(l => l.VirtualizeAt, virtualizeAt));

    [Fact]
    public void A_football_play_leads_with_its_headline_then_the_down_then_the_sentence()
    {
        var cut = Render([Pass(1)]);

        var body = cut.Find(".gc__play .gc__body");
        Assert.Equal(new[] { "gc__headline", "gc__dd", "gc__text" }, body.Children.Select(c => c.ClassName).ToArray());
        Assert.Equal("55-yard pass", body.Children[0].TextContent);
        Assert.Equal("1st & 10 at CAR 35", body.Children[1].TextContent);
    }

    [Fact]
    public void A_play_with_nothing_worth_heading_draws_no_headline()
    {
        var cut = Render([FakeGamecastData.Play(1, 1, "Something the feed did not type") with { Type = null }]);

        Assert.Empty(cut.FindAll(".gc__headline"));
        Assert.Equal("Something the feed did not type", cut.Find(".gc__text").TextContent);
    }

    [Fact]
    public void Another_league_s_plays_are_not_headed()
    {
        var cut = Render([FakeGamecastData.Play(1, 1, "END PERIOD") with { Type = "End Period" }], sport: "nba");

        Assert.Empty(cut.FindAll(".gc__headline"));
    }

    private static GamecastPlay Win(long sequence, double? home) => Pass(sequence) with { Id = sequence, HomeWinPct = home };

    private IRenderedComponent<PlayLog> RenderSwings(IReadOnlyList<GamecastPlay> plays, IReadOnlyList<GamecastPlay>? game = null)
        => RenderComponent<PlayLog>(p =>
        {
            p.Add(l => l.Plays, plays.ToList()).Add(l => l.Sport, "nfl").Add(l => l.HomeAbbrev, "WSH").Add(l => l.AwayAbbrev, "CIN");
            if (game is not null)
                p.Add(l => l.Game, game);
        });

    private static string? Swing(IRenderedComponent<PlayLog> cut, long id)
        => cut.Find($".gc__play[data-play='{id}']").QuerySelector(".gc__swing")?.QuerySelector("[aria-hidden='true']")?.TextContent;

    /// <summary>
    /// A play that moved the win probability five points or more says so at the end of its row,
    /// from the side that gained — "WSH +14" — and in words for a reader who cannot see it; a
    /// smaller move, or a play either side of which has no probability, says nothing.
    /// </summary>
    [Fact]
    public void A_play_that_swung_the_win_probability_five_points_says_who_gained()
    {
        var plays = new[] { Win(1, .50), Win(2, .64), Win(3, .61), Win(4, .40), Win(5, null), Win(6, .70) };

        var cut = RenderSwings(plays);

        Assert.Null(Swing(cut, 1));
        Assert.Equal("WSH +14", Swing(cut, 2));
        Assert.Null(Swing(cut, 3));
        Assert.Equal("CIN +21", Swing(cut, 4));
        Assert.Null(Swing(cut, 5));
        Assert.Null(Swing(cut, 6));

        Assert.Equal("Win probability: WSH up 14 points", cut.Find(".gc__play[data-play='2'] .gc__swing .sr-only").TextContent);
    }

    /// <summary>
    /// The swing is from the play before in the game, whatever order the log is read in (newest
    /// first while the game is on) and whichever plays it holds: a drive's first play measures from
    /// the last play of the drive before it when the window hands the log the whole game.
    /// </summary>
    [Fact]
    public void The_swing_is_from_the_play_before_in_the_game_not_in_the_list()
    {
        var game = new[] { Win(1, .50), Win(2, .52), Win(3, .40), Win(4, .41) };

        var newestFirst = RenderSwings([game[3], game[2]]);
        Assert.Null(Swing(newestFirst, 3));
        Assert.Null(Swing(newestFirst, 4));

        var withGame = RenderSwings([game[3], game[2]], game);
        Assert.Equal("CIN +12", Swing(withGame, 3));
        Assert.Null(Swing(withGame, 4));
    }

    [Fact]
    public void A_virtualised_log_heads_its_rows_the_same()
    {
        var plays = Enumerable.Range(1, 5).Select(i => Pass(i)).ToList();

        var cut = Render(plays, virtualizeAt: 2);

        Assert.NotEmpty(cut.FindAll(".gc__play"));
        Assert.All(cut.FindAll(".gc__play"), row => Assert.Equal("55-yard pass", row.QuerySelector(".gc__headline")!.TextContent));
    }
}
