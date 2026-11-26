using Bunit;
using XLines.Data.CrossReference;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The Gamecast's game leaders, against lines from CIN @ WSH (game 2049, final) as the box score
/// hands them out — keys qualified by <see cref="StatLineKeys"/>: the leaders read Burrow 300 and
/// Daniels 289 passing, A.Williams 122 receiving; the away side is on the left; a passer's sacks
/// taken are never a sack leader; a side with no one shows a dash, a category no one has is left
/// out, an empty box draws nothing; and a leader's name opens the player.
/// </summary>
public class GameLeadersTests : DeskTestContext
{
    private static Dictionary<string, string> S(params (string Key, string Value)[] stats)
        => stats.ToDictionary(s => s.Key, s => s.Value);

    // CIN, the away side.
    private static readonly BoxScoreLine Burrow = new(11, "Joe Burrow", "QB", false, S(
        ("CAR", "2"), ("QBR", "56.9"), ("RTG", "108.3"), ("LONG", "0"), ("C/ATT", "28/40"),
        ("passing.SACKS", "3-19"), ("passing.INT", "0"), ("passing.TD", "2"), ("rushing.TD", "0"),
        ("passing.AVG", "7.5"), ("passing.YDS", "300"), ("rushing.AVG", "-0.5"), ("rushing.YDS", "-1")));

    private static readonly BoxScoreLine Perine = new(12, "Samaje Perine", "RB", false, S(
        ("PD", "0"), ("CAR", "6"), ("FUM", "1"), ("TFL", "0"), ("TOT", "1"), ("LOST", "0"), ("SOLO", "0"),
        ("TGTS", "4"), ("defensive.SACKS", "0"), ("QB HTS", "0"), ("rushing.TD", "0"), ("fumbles.REC", "0"),
        ("rushing.AVG", "4.3"), ("rushing.YDS", "26"), ("defensive.TD", "0"), ("receiving.TD", "0"),
        ("rushing.LONG", "11"), ("receiving.AVG", "17.8"), ("receiving.REC", "4"), ("receiving.YDS", "71"),
        ("receiving.LONG", "44")));

    private static readonly BoxScoreLine Gesicki = new(13, "Mike Gesicki", "TE", false, S(
        ("receiving.TD", "0"), ("receiving.AVG", "15.4"), ("receiving.REC", "5"), ("receiving.YDS", "77"),
        ("receiving.LONG", "32"), ("TGTS", "6")));

    private static readonly BoxScoreLine Mafe = new(14, "Boye Mafe", "DE", false, S(
        ("PD", "0"), ("defensive.TD", "0"), ("TFL", "3"), ("TOT", "5"), ("SOLO", "4"), ("defensive.SACKS", "3"), ("QB HTS", "3")));

    private static readonly BoxScoreLine Carter = new(15, "Barrett Carter", "LB", false, S(
        ("PD", "0"), ("defensive.TD", "0"), ("TFL", "0"), ("TOT", "10"), ("SOLO", "3"), ("defensive.SACKS", "0"), ("QB HTS", "0")));

    // WSH, the home side.
    private static readonly BoxScoreLine Daniels = new(21, "Jayden Daniels", "QB", true, S(
        ("CAR", "6"), ("QBR", "58.6"), ("RTG", "91.2"), ("LONG", "47"), ("C/ATT", "23/35"),
        ("passing.SACKS", "3-11"), ("passing.INT", "0"), ("passing.TD", "0"), ("rushing.TD", "0"),
        ("passing.AVG", "8.3"), ("passing.YDS", "289"), ("rushing.AVG", "9.2"), ("rushing.YDS", "55")));

    private static readonly BoxScoreLine CroskeyMerritt = new(22, "Jacory Croskey-Merritt", "RB", true, S(
        ("CAR", "13"), ("TGTS", "3"), ("rushing.TD", "1"), ("rushing.AVG", "4.2"), ("rushing.YDS", "54"),
        ("receiving.TD", "0"), ("rushing.LONG", "9"), ("receiving.AVG", "11.5"), ("receiving.REC", "2"),
        ("receiving.YDS", "23"), ("receiving.LONG", "18")));

    private static readonly BoxScoreLine Williams = new(23, "Antonio Williams", "WR", true, S(
        ("receiving.TD", "0"), ("receiving.AVG", "13.6"), ("receiving.REC", "9"), ("receiving.YDS", "122"),
        ("receiving.LONG", "42"), ("TGTS", "11")));

    private static readonly BoxScoreLine Settle = new(24, "Tim Settle", "DT", true, S(
        ("PD", "0"), ("defensive.TD", "0"), ("TFL", "1"), ("TOT", "5"), ("SOLO", "2"), ("defensive.SACKS", "1.5"), ("QB HTS", "2")));

    private static readonly BoxScoreLine Luvu = new(25, "Frankie Luvu", "LB", true, S(
        ("PD", "0"), ("defensive.TD", "0"), ("TFL", "0"), ("TOT", "12"), ("SOLO", "5"), ("defensive.SACKS", "0"), ("QB HTS", "1")));

    private static GameBoxScore Box(IReadOnlyList<BoxScoreLine> away, IReadOnlyList<BoxScoreLine> home)
        => new(BoardRows.Game(), home, away);

    private static readonly GameBoxScore Final = Box(
        [Burrow, Perine, Gesicki, Mafe, Carter],
        [Daniels, CroskeyMerritt, Williams, Settle, Luvu]);

    private (int PlayerId, string Name)? _opened;

    private IRenderedComponent<GameLeaders> Render(GameBoxScore? box, string? sport = "nfl")
        => RenderComponent<GameLeaders>(p => p
            .Add(s => s.Box, box).Add(s => s.AwayAbbrev, "CIN").Add(s => s.HomeAbbrev, "WSH").Add(s => s.Sport, sport)
            .Add(s => s.OnPlayer, ((int PlayerId, string Name) player) => _opened = player));

    private static string Text(AngleSharp.Dom.IElement row, string side, string part)
        => row.QuerySelector($".leaders__side--{side} .leaders__{part}")?.TextContent.Trim() ?? string.Empty;

    [Fact]
    public void The_leaders_of_a_final_read_as_espn_has_them()
    {
        var rows = GameLeadersModel.For(Final, "nfl");

        Assert.Equal(new[] { "Passing yards", "Rushing yards", "Receiving yards", "Sacks", "Tackles" }, rows.Select(r => r.Label));

        Assert.Equal(new GameLeader(11, "Joe Burrow", "300", "28/40, 2 TD"), rows[0].Away);
        Assert.Equal(new GameLeader(21, "Jayden Daniels", "289", "23/35"), rows[0].Home);

        Assert.Equal(new GameLeader(12, "Samaje Perine", "26", "6 car"), rows[1].Away);
        // The passer out-ran the back: 55 on 6 against 54 on 13.
        Assert.Equal(new GameLeader(21, "Jayden Daniels", "55", "6 car"), rows[1].Home);

        Assert.Equal(new GameLeader(13, "Mike Gesicki", "77", "5 rec"), rows[2].Away);
        Assert.Equal(new GameLeader(23, "Antonio Williams", "122", "9 rec"), rows[2].Home);

        // Burrow's "3-19" is the sacks he took; only a defender's line leads here.
        Assert.Equal(new GameLeader(14, "Boye Mafe", "3", "5 tkl, 3 TFL"), rows[3].Away);
        Assert.Equal(new GameLeader(24, "Tim Settle", "1.5", "5 tkl, 1 TFL"), rows[3].Home);

        Assert.Equal(new GameLeader(15, "Barrett Carter", "10", "3 solo"), rows[4].Away);
        Assert.Equal(new GameLeader(25, "Frankie Luvu", "12", "5 solo"), rows[4].Home);
    }

    [Fact]
    public void Away_draws_at_the_leading_edge_and_home_at_the_trailing()
    {
        var cut = Render(Final);

        var passing = cut.Find(".leaders__row[data-row='Passing yards']");
        Assert.Equal("Passing yards", passing.QuerySelector(".leaders__label")!.TextContent);
        Assert.Equal("300", Text(passing, "away", "value"));
        Assert.Equal("Joe Burrow", passing.QuerySelector(".leaders__side--away .desklink__label")!.TextContent);
        Assert.Equal("28/40, 2 TD", Text(passing, "away", "line"));
        Assert.Equal("289", Text(passing, "home", "value"));
        Assert.Equal("Jayden Daniels", passing.QuerySelector(".leaders__side--home .desklink__label")!.TextContent);

        var receiving = cut.Find(".leaders__row[data-row='Receiving yards']");
        Assert.Equal("122", Text(receiving, "home", "value"));
        Assert.Equal("9 rec", Text(receiving, "home", "line"));

        // The crests are hidden; each side says its team where it is read.
        Assert.Equal("true", cut.Find(".leaders__head").GetAttribute("aria-hidden"));
        Assert.Equal("CIN", passing.QuerySelector(".leaders__side--away .sr-only")!.TextContent.Trim());
    }

    [Fact]
    public void A_leader_s_name_is_a_button_that_opens_the_player()
    {
        var cut = Render(Final);

        var name = cut.Find(".leaders__row[data-row='Receiving yards'] .leaders__side--home button.desklink");
        name.Click();

        Assert.Equal((23, "Antonio Williams"), _opened);
    }

    [Fact]
    public void A_side_with_no_one_in_a_category_shows_a_dash_and_a_category_no_one_has_is_left_out()
    {
        // WSH threw and no one caught; CIN only caught. Nobody on either side ran, tackled or sacked.
        var box = Box([Gesicki], [new BoxScoreLine(21, "Jayden Daniels", "QB", true, S(("C/ATT", "1/3"), ("passing.YDS", "9")))]);
        var cut = Render(box);

        Assert.Equal(new[] { "Passing yards", "Receiving yards" }, cut.FindAll(".leaders__row").Select(r => r.GetAttribute("data-row")));

        var passing = cut.Find(".leaders__row[data-row='Passing yards']");
        Assert.Equal("—", Text(passing, "away", "none"));
        Assert.Empty(passing.QuerySelectorAll(".leaders__side--away button"));
        Assert.Equal("9", Text(passing, "home", "value"));

        Assert.Equal("—", Text(cut.Find(".leaders__row[data-row='Receiving yards']"), "home", "none"));
    }

    [Fact]
    public void A_zero_names_no_sack_or_tackle_leader_but_yards_lead_whatever_they_are()
    {
        var rows = GameLeadersModel.For(Box([Carter with { Stats = S(("TOT", "0"), ("defensive.SACKS", "0")) }, Burrow], []), "nfl");

        // Burrow's −1 still leads CIN's rushing; Carter's zeros lead nothing.
        Assert.Equal(new[] { "Passing yards", "Rushing yards" }, rows.Select(r => r.Label));
        Assert.Equal("-1", rows[1].Away!.Value);
        Assert.Equal("2 car", rows[1].Away!.Line);
        Assert.Null(rows[1].Home);
    }

    [Fact]
    public void A_tie_keeps_the_first_line_the_box_lists()
    {
        var rows = GameLeadersModel.For(Box([Gesicki, Gesicki with { PlayerId = 99, Name = "Second" }], []), "nfl");

        Assert.Equal(13, rows.Single().Away!.PlayerId);
    }

    [Fact]
    public void An_empty_or_missing_box_draws_nothing()
    {
        Assert.Equal(string.Empty, Render(null).Markup.Trim());
        Assert.Equal(string.Empty, Render(Box([], [])).Markup.Trim());
    }

    [Fact]
    public void A_league_whose_box_is_not_football_s_draws_nothing()
        => Assert.Equal(string.Empty, Render(Final, "nba").Markup.Trim());
}
