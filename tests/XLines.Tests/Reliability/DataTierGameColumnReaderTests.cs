using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.CrossReference;
using Microsoft.EntityFrameworkCore;

namespace XLines.Tests.Reliability;

/// <summary>
/// The columns ESPN and MLB write onto a game (ADR 0024, amendment of 2026-11-15), held on the
/// readers that hand a <see cref="Game"/> back besides the board: the box score, the matchup's
/// form and the game search. <see cref="DataTierRemainingReaderTests"/> holds the board's.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class DataTierGameColumnReaderTests(PostgresFixture fixture)
{
    private sealed class Visitor : IDataTier
    {
        public bool CanSeePersonalUseSources => false;
    }

    private static readonly IDataTier Operator = FullDataTier.Instance;
    private static readonly IDataTier Guest = new Visitor();

    private sealed record Scaffold(Team Home, Team Away, Game Game, string Suffix);

    /// <summary>
    /// A game under way with ESPN's state of play and MLB's probables on it, and one box-score
    /// line from a displayable source, so the box score exists for a visitor too and the only
    /// thing the tier changes is the game's own columns.
    /// </summary>
    private static async Task<Scaffold> SeedAsync(XLinesDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var sport = new Sport { Key = $"tier3-{suffix}", Name = "TEST" };
        db.Sports.Add(sport);
        await db.SaveChangesAsync();

        var home = new Team { SportId = sport.Id, Name = $"Home {suffix}", Abbrev = "HOM" };
        var away = new Team { SportId = sport.Id, Name = $"Away {suffix}", Abbrev = "AWY" };
        db.Teams.AddRange(home, away);

        var market = new Source
        {
            Key = $"tier3-market-{suffix}",
            Name = "Market",
            Kind = SourceKind.Odds,
            BaseUrl = "local://market",
            Licence = SourceLicence.Displayable
        };
        db.Sources.Add(market);
        await db.SaveChangesAsync();

        var game = new Game
        {
            SportId = sport.Id,
            HomeTeamId = home.Id,
            AwayTeamId = away.Id,
            StartsAt = DateTimeOffset.UtcNow.AddHours(-1),
            Status = GameStatus.Live,
            HomeScore = 4,
            AwayScore = 2,
            SeasonYear = 2026,
            HomeProbablePitcher = "Chris Sale",
            AwayProbablePitcher = "Kodai Senga",
            Period = 6,
            StatusDetail = "Top 6th",
            LastPlay = "Ground out to short",
            Linescore = """{"home":[1,3],"away":[0,2]}""",
            Situation = """{"balls":1,"strikes":2,"outs":1}""",
            LiveUpdatedAt = DateTimeOffset.UtcNow
        };
        db.Games.Add(game);
        await db.SaveChangesAsync();

        var player = new Player { SportId = sport.Id, TeamId = home.Id, FullName = $"Batter {suffix}" };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        db.PlayerGameStats.Add(new PlayerGameStat
        {
            PlayerId = player.Id,
            GameId = game.Id,
            SourceId = market.Id,
            TeamId = home.Id,
            StatLine = """{"H":"2","AB":"4"}""",
            CapturedAt = game.StartsAt
        });
        await db.SaveChangesAsync();

        return new Scaffold(home, away, game, suffix);
    }

    [Fact]
    public async Task A_visitors_box_score_game_carries_no_state_of_play_and_no_probables()
    {
        await using var db = fixture.CreateContext();
        var s = await SeedAsync(db);

        AssertPresent((await new GameLogService(db, Operator).BoxScoreAsync(s.Game.Id))!.Game);

        var visitor = (await new GameLogService(db, Guest).BoxScoreAsync(s.Game.Id))!;
        AssertBlank(visitor.Game);
        Assert.Single(visitor.Home);
    }

    [Fact]
    public async Task A_visitors_matchup_game_carries_no_state_of_play_and_no_probables()
    {
        await using var db = fixture.CreateContext();
        var s = await SeedAsync(db);

        AssertPresent((await new MatchupCrossReference(db, Operator).GetAsync(s.Game.Id, TimeSpan.FromDays(30)))!.Game);
        AssertBlank((await new MatchupCrossReference(db, Guest).GetAsync(s.Game.Id, TimeSpan.FromDays(30)))!.Game);
    }

    [Fact]
    public async Task A_visitors_searched_game_carries_no_state_of_play_and_no_probables()
    {
        await using var db = fixture.CreateContext();
        var s = await SeedAsync(db);

        AssertPresent(Assert.Single(await new SearchService(db, Operator).GamesAsync(s.Suffix)));
        AssertPresent(Assert.Single(await new SearchService(db, Operator).GamesAsync(s.Suffix, fromToday: true)));
        AssertBlank(Assert.Single(await new SearchService(db, Guest).GamesAsync(s.Suffix)));
        AssertBlank(Assert.Single(await new SearchService(db, Guest).GamesAsync(s.Suffix, fromToday: true)));
    }

    private static void AssertPresent(Game game)
    {
        Assert.Equal("Chris Sale", game.HomeProbablePitcher);
        Assert.Equal("Kodai Senga", game.AwayProbablePitcher);
        Assert.Equal(6, game.Period);
        Assert.Equal("Top 6th", game.StatusDetail);
        Assert.Equal("Ground out to short", game.LastPlay);
        Assert.NotNull(game.Linescore);
        Assert.NotNull(game.Situation);
        Assert.NotNull(game.LiveUpdatedAt);
    }

    private static void AssertBlank(Game game)
    {
        Assert.Null(game.HomeProbablePitcher);
        Assert.Null(game.AwayProbablePitcher);
        Assert.Null(game.Period);
        Assert.Null(game.StatusDetail);
        Assert.Null(game.LastPlay);
        Assert.Null(game.Linescore);
        Assert.Null(game.Situation);
        Assert.Null(game.LiveUpdatedAt);

        // The spine stays: the fixture and its score.
        Assert.Equal(GameStatus.Live, game.Status);
        Assert.Equal(4, game.HomeScore);
        Assert.Equal(2, game.AwayScore);
    }
}
