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

    [Fact]
    public void A_virtualised_log_heads_its_rows_the_same()
    {
        var plays = Enumerable.Range(1, 5).Select(i => Pass(i)).ToList();

        var cut = Render(plays, virtualizeAt: 2);

        Assert.NotEmpty(cut.FindAll(".gc__play"));
        Assert.All(cut.FindAll(".gc__play"), row => Assert.Equal("55-yard pass", row.QuerySelector(".gc__headline")!.TextContent));
    }
}
