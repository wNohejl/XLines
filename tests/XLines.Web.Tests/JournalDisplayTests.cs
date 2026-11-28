using XLines.Core.Entities;
using XLines.Desk.Primitives;
using XLines.Web.Components.Panels;

namespace XLines.Web.Tests;

/// <summary>
/// What a Journal row says, read off the row alone (JournalDisplay): the club a selection backs,
/// the bet in words, the close a CLV was measured against, the sides a tracked game offers and a
/// result's tag. Pure functions, so pinned here without a render.
/// </summary>
public class JournalDisplayTests
{
    private static Game Game(string sport = "nfl") => new()
    {
        Id = 21,
        Sport = new Sport { Key = sport, Name = sport.ToUpperInvariant() },
        AwayTeam = new Team { Name = "Buffalo Bills", Abbrev = "BUF" },
        HomeTeam = new Team { Name = "Kansas City Chiefs", Abbrev = "KC" },
    };

    [Fact]
    public void A_tracked_game_offers_away_then_home_valued_by_name()
    {
        var sides = JournalDisplay.TeamSides(Game());

        Assert.NotNull(sides);
        Assert.Equal(["Buffalo Bills", "Kansas City Chiefs"], sides.Select(s => s.Value));
        Assert.Equal(["BUF", "KC"], sides.Select(s => s.Label));
    }

    [Fact]
    public void A_game_without_both_teams_offers_no_sides()
    {
        Assert.Null(JournalDisplay.TeamSides(null));
        Assert.Null(JournalDisplay.TeamSides(new Game { AwayTeam = new Team { Name = "Buffalo Bills", Abbrev = "BUF" } }));
    }

    [Theory]
    [InlineData("Buffalo Bills", "BUF")]
    [InlineData("kansas city chiefs", "KC")]
    public void A_selection_backs_the_club_it_names(string outcome, string abbrev)
    {
        var club = JournalDisplay.SelectedClub(new JournalEntry { Game = Game(), Outcome = outcome });

        Assert.Equal(abbrev, club?.Abbrev);
    }

    [Fact]
    public void A_total_a_bout_and_an_untracked_bet_back_no_club()
    {
        Assert.Null(JournalDisplay.SelectedClub(new JournalEntry { Game = Game(), Outcome = "Over" }));
        Assert.Null(JournalDisplay.SelectedClub(new JournalEntry { Game = Game("ufc"), Outcome = "Buffalo Bills" }));
        Assert.Null(JournalDisplay.SelectedClub(new JournalEntry { Outcome = "Buffalo Bills" }));
    }

    [Fact]
    public void A_bet_is_described_by_its_side_line_and_market_or_by_its_own_words()
    {
        Assert.Equal("Buffalo Bills -3 Spread",
            JournalDisplay.Describe(new JournalEntry { Outcome = "Buffalo Bills", LineTaken = -3m, Market = Markets.Spread }));
        Assert.Equal("Buffalo Bills ML",
            JournalDisplay.Describe(new JournalEntry { Outcome = "Buffalo Bills", Market = Markets.Moneyline }));
        Assert.Equal("Player X over 1.5 receiving TDs",
            JournalDisplay.Describe(new JournalEntry { Outcome = "—", Market = "other", FreeTextMarket = "Player X over 1.5 receiving TDs" }));
    }

    [Fact]
    public void The_clv_title_names_the_close_and_the_book_it_came_from()
    {
        var entry = new JournalEntry
        {
            Outcome = "Buffalo Bills",
            Market = Markets.Spread,
            Book = "draftkings",
            LineTaken = -1.5m,
            PriceTaken = -110m,
            ClosingPoints = -2.5m,
            ClosingPrice = -120,
            ClosingBook = "fanduel",
        };

        Assert.StartsWith("Took -1.5 -110; closed -2.5 -120 at fanduel (no close for draftkings)", JournalDisplay.ClvTitle(entry));
    }

    [Fact]
    public void The_clv_title_does_not_name_the_book_when_it_closed_there()
    {
        var entry = new JournalEntry
        {
            Outcome = "Buffalo Bills",
            Market = Markets.Moneyline,
            Book = "draftkings",
            PriceTaken = 150m,
            ClosingPrice = 140,
            ClosingBook = "DraftKings",
        };

        Assert.StartsWith("Took +150; closed +140", JournalDisplay.ClvTitle(entry));
        Assert.DoesNotContain(" at ", JournalDisplay.ClvTitle(entry).Split('.')[0]);
    }

    [Theory]
    [InlineData(EntryResult.Win, DeskState.Positive)]
    [InlineData(EntryResult.Loss, DeskState.Negative)]
    [InlineData(EntryResult.Push, DeskState.Info)]
    [InlineData(EntryResult.Void, DeskState.Neutral)]
    [InlineData(EntryResult.Pending, DeskState.Warning)]
    public void Each_result_has_its_tag_state(EntryResult result, DeskState state)
        => Assert.Equal(state, JournalDisplay.ResultState(result));

    [Fact]
    public void A_parlay_with_a_leg_still_pending_is_not_stuck()
    {
        var parlay = new Parlay
        {
            Legs =
            [
                new JournalEntry { Id = 1, Result = EntryResult.Win },
                new JournalEntry { Id = 2, Result = EntryResult.Pending },
            ]
        };

        Assert.Null(JournalDisplay.Stuck(parlay));
    }
}
