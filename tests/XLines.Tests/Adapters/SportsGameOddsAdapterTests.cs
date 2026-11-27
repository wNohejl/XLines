using System.Net;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Ingestion;
using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace XLines.Tests.Adapters;

/// <summary>
/// The second price source (ADR 0033), pinned against a fixture built from the field names the
/// provider documents (sportsgameodds.com/docs: getEvents, data-types/odds, data-types/markets) —
/// not a captured response, since no key was held. What goes on the wire, what is read back and
/// under which book, what a pull is billed, how it fails, and that without a key nothing is there.
/// </summary>
public class SportsGameOddsAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 16, 0, 0, TimeSpan.Zero);

    private static (SportsGameOddsAdapter Adapter, StubHttp Handler) Build(
        Func<string, (HttpStatusCode, string)>? respond = null, Action<SportsGameOddsOptions>? configure = null)
    {
        var handler = new StubHttp(respond ?? (_ => (HttpStatusCode.OK, Fixtures.Text("sportsgameodds.events.json"))));

        var options = new IngestionOptions();
        options.SportsGameOdds.ApiKey = "sgo-test-key";
        options.SportsGameOdds.PageDelay = TimeSpan.Zero;
        configure?.Invoke(options.SportsGameOdds);

        var adapter = new SportsGameOddsAdapter(handler.Client(SportsGameOddsAdapter.BaseUrl), Options.Create(options),
            NullLogger<SportsGameOddsAdapter>.Instance, TimeProvider.System);
        return (adapter, handler);
    }

    private static SportsGameOddsAdapter.Page Read(string? body = null)
        => SportsGameOddsAdapter.ReadPage(body ?? Fixtures.Text("sportsgameodds.events.json"), "nfl", Markets.V1, ["caesars"], Now);

    [Fact]
    public void Caesars_lands_as_the_desk_s_one_Caesars_key()
    {
        var page = Read();

        Assert.All(page.Odds, o => Assert.Equal("williamhill_us", o.Book));
        Assert.Equal("williamhill_us", SportsGameOddsAdapter.CanonicalBook("caesars"));
        Assert.Equal("unibet_us", SportsGameOddsAdapter.CanonicalBook("unibet"));
        Assert.Equal("draftkings", SportsGameOddsAdapter.CanonicalBook("draftkings"));
    }

    [Fact]
    public void Each_full_game_market_is_read_with_its_sides_and_lines()
    {
        var odds = Read().Odds;

        var homeMl = Assert.Single(odds, o => o.Market == Markets.Moneyline && o.Outcome == "Kansas City Chiefs");
        Assert.Equal(-120, homeMl.PriceAmerican);
        Assert.Null(homeMl.Line);
        // When the book moved, as the provider stamps it.
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 15, 4, 0, TimeSpan.Zero), homeMl.CapturedAt);

        var awayMl = Assert.Single(odds, o => o.Market == Markets.Moneyline && o.Outcome == "Buffalo Bills");
        Assert.Equal(100, awayMl.PriceAmerican);
        Assert.Equal(Now, awayMl.CapturedAt);

        var spread = Assert.Single(odds, o => o.Market == Markets.Spread);
        Assert.Equal(("Kansas City Chiefs", -1.5m, -110), (spread.Outcome, spread.Line!.Value, spread.PriceAmerican));

        Assert.Equal(-105, Assert.Single(odds, o => o.Outcome == "over").PriceAmerican);
        Assert.Equal(47.5m, Assert.Single(odds, o => o.Outcome == "under").Line);
    }

    [Fact]
    public void Only_the_books_asked_for_unavailable_lines_and_other_periods_are_left_out()
    {
        var odds = Read().Odds;

        // Five of the six: the away spread is marked unavailable; DraftKings comes from The Odds API;
        // the first-half moneyline is not a full-game market.
        Assert.Equal(5, odds.Count);
        Assert.DoesNotContain(odds, o => o.PriceAmerican == -112);
        Assert.DoesNotContain(odds, o => o.PriceAmerican == -125);
    }

    [Fact]
    public void A_started_game_and_an_event_with_no_teams_are_not_games_but_are_billed()
    {
        var page = Read();

        var game = Assert.Single(page.Games);
        Assert.Equal(("YlZBEbhTgV80ZeTIIyqm", "Kansas City Chiefs", "Buffalo Bills"), (game.SourceGameId, game.HomeTeamName, game.AwayTeamName));
        Assert.Equal(new DateTimeOffset(2026, 11, 29, 21, 25, 0, TimeSpan.Zero), game.StartsAt);
        // Every event returned is an object, read or not.
        Assert.Equal(3, page.Listed);
        Assert.Equal("n.1763687700000.4lronEc61vA9pcyqoRIa", page.NextCursor);
    }

    [Fact]
    public void Markets_not_asked_for_are_not_read()
    {
        var page = SportsGameOddsAdapter.ReadPage(Fixtures.Text("sportsgameodds.events.json"), "nfl", [Markets.Moneyline], ["caesars"], Now);

        Assert.All(page.Odds, o => Assert.Equal(Markets.Moneyline, o.Market));
        Assert.Equal(["points-home-game-ml-home", "points-away-game-ml-away"], SportsGameOddsAdapter.OddIds([Markets.Moneyline]));
    }

    [Fact]
    public void Numbers_are_read_as_the_provider_writes_them_and_as_numbers_too()
    {
        var page = Read("""
            { "success": true, "data": [ { "eventID": "e1",
              "teams": { "home": { "names": { "long": "A" } }, "away": { "names": { "long": "B" } } },
              "status": { "startsAt": "2026-11-29T18:00:00Z" },
              "odds": {
                "points-home-game-ml-home": { "byBookmaker": { "caesars": { "odds": 150 } } },
                "points-away-game-ml-away": { "byBookmaker": { "caesars": { "odds": "even" } } },
                "points-home-game-sp-home": { "byBookmaker": { "caesars": { "odds": "-110" } } },
                "points-all-game-ou-over": { "byBookmaker": { "caesars": { "odds": { "x": 1 }, "overUnder": "8" } } }
              } } ] }
            """);

        Assert.Equal([150, 100], page.Odds.Select(o => o.PriceAmerican));
    }

    [Fact]
    public void An_error_envelope_is_the_provider_s_own_words()
    {
        var page = Read("""{ "success": false, "error": "Invalid API key" }""");

        Assert.Equal("Invalid API key", page.Error);
        Assert.Equal(0, page.Listed);
    }

    [Fact]
    public async Task The_key_goes_in_a_header_and_never_in_the_address()
    {
        var (adapter, handler) = Build(_ => (HttpStatusCode.OK, """{ "success": true, "data": [] }"""));

        await adapter.FetchSlateAsync("nfl", Markets.V1, CancellationToken.None);

        var asked = handler.Last!;
        Assert.DoesNotContain("sgo-test-key", asked.ToString());
        Assert.Equal("sgo-test-key", handler.Headers[^1]["X-Api-Key"]);

        var query = System.Web.HttpUtility.ParseQueryString(asked.Query);
        Assert.Equal("/v2/events", asked.AbsolutePath);
        Assert.Equal("NFL", query["leagueID"]);
        Assert.Equal("caesars", query["bookmakerID"]);
        Assert.Equal(6, query["oddID"]!.Split(',').Length);
        Assert.Equal("false", query["started"]);
        Assert.Equal("50", query["limit"]);
        Assert.NotNull(query["startsBefore"]);
    }

    [Fact]
    public async Task A_pull_is_billed_an_object_per_event_and_stops_at_its_ceiling()
    {
        var pages = 0;
        var (adapter, handler) = Build(_ =>
        {
            pages++;
            return (HttpStatusCode.OK, Fixtures.Text("sportsgameodds.events.json"));
        });
        adapter.EventCeiling = 5;

        var result = await adapter.FetchSlateAsync("nfl", Markets.V1, CancellationToken.None);

        // Three events a page, a cursor every time: two pages reach the ceiling of five, and the
        // second asks only for what is left under it.
        Assert.Equal(2, pages);
        Assert.Equal("2", System.Web.HttpUtility.ParseQueryString(handler.Last!.Query)["limit"]);
        Assert.Equal("n.1763687700000.4lronEc61vA9pcyqoRIa", System.Web.HttpUtility.ParseQueryString(handler.Last.Query)["cursor"]);
        Assert.Equal(new FetchCost(Requests: 2, Credits: 6, CreditsRemaining: null), result.Cost);
    }

    [Fact]
    public async Task A_refused_key_fails_the_run_in_plain_words_and_returns_nothing_to_write()
    {
        var (adapter, _) = Build(_ => (HttpStatusCode.Unauthorized, """{ "success": false, "error": "Invalid API key" }"""));

        var thrown = await Assert.ThrowsAsync<OddsFetchFailedException>(
            () => adapter.FetchSlateAsync("nba", Markets.V1, CancellationToken.None));

        Assert.Contains("401", thrown.Message);
        Assert.Contains("Invalid API key", thrown.Message);
        Assert.Contains("Nothing was written", thrown.Message);
        Assert.Equal(0, thrown.Cost.Credits);
    }

    [Fact]
    public async Task A_later_page_failing_fails_the_whole_pull_and_carries_what_was_billed()
    {
        var pages = 0;
        var (adapter, _) = Build(_ => ++pages == 1
            ? (HttpStatusCode.OK, Fixtures.Text("sportsgameodds.events.json"))
            : (HttpStatusCode.TooManyRequests, """{ "success": false, "error": "Rate limit" }"""));

        var thrown = await Assert.ThrowsAsync<OddsFetchFailedException>(
            () => adapter.FetchSlateAsync("nfl", Markets.V1, CancellationToken.None));

        Assert.Contains("10 requests a minute", thrown.Message);
        Assert.Equal(3, thrown.Cost.Credits);
    }

    [Fact]
    public async Task Events_listed_and_none_readable_is_drift_not_a_quiet_day()
    {
        var (adapter, _) = Build(_ => (HttpStatusCode.OK, """{ "success": true, "data": [ { "id": "a" }, { "id": "b" } ] }"""));

        var thrown = await Assert.ThrowsAsync<OddsFetchFailedException>(
            () => adapter.FetchSlateAsync("mlb", Markets.V1, CancellationToken.None));

        Assert.Contains("schema drift", thrown.Message);
        Assert.Equal(2, thrown.Cost.Credits);
    }

    [Fact]
    public async Task A_body_that_is_not_json_fails_as_a_billed_call()
    {
        var (adapter, _) = Build(_ => (HttpStatusCode.OK, "<html>maintenance</html>"));

        var thrown = await Assert.ThrowsAsync<OddsFetchFailedException>(
            () => adapter.FetchSlateAsync("nfl", Markets.V1, CancellationToken.None));

        Assert.Contains("could not be read", thrown.Message);
    }

    [Fact]
    public async Task UFC_is_not_offered_and_nothing_is_asked()
    {
        var (adapter, handler) = Build();

        Assert.False(SportsGameOddsAdapter.Covers("ufc"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.FetchSlateAsync("ufc", Markets.V1, CancellationToken.None));
        Assert.Empty(handler.Asked);
    }

    // ---- Registration ---------------------------------------------------------------------------

    private static ServiceProvider Container(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXLinesIngestion(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Without_a_key_it_is_not_registered()
    {
        using var provider = Container(new Dictionary<string, string?> { ["Ingestion:SportsGameOdds:ApiKey"] = " " });
        using var scope = provider.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<SportsGameOddsAdapter>());
        Assert.Null(scope.ServiceProvider.GetService<SportsGameOddsBudget>());
    }

    [Fact]
    public void With_a_key_it_is_registered_by_name_and_never_among_the_odds_sources()
    {
        using var provider = Container(new Dictionary<string, string?> { ["Ingestion:SportsGameOdds:ApiKey"] = "k" });
        using var scope = provider.CreateScope();

        var adapter = scope.ServiceProvider.GetRequiredService<SportsGameOddsAdapter>();
        Assert.True(adapter.IsConfigured);

        // Every reader of the odds sources spends The Odds API's credits; this one is not among them.
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<IOddsSource>(), s => s.Key == SportsGameOddsAdapter.SourceKey);

        var registry = new SourceRegistry(scope.ServiceProvider.GetServices<IOddsSource>(), [], adapter);
        Assert.Same(adapter, registry.SportsGameOdds);
        Assert.DoesNotContain(registry.OddsSources, s => s.Key == SportsGameOddsAdapter.SourceKey);
    }

    [Fact]
    public void The_registry_resolves_with_and_without_it()
    {
        using var without = Container([]);
        using (var scope = without.CreateScope())
            Assert.Null(scope.ServiceProvider.GetRequiredService<SourceRegistry>().SportsGameOdds);

        using var with = Container(new Dictionary<string, string?> { ["Ingestion:SportsGameOdds:ApiKey"] = "k" });
        using (var scope = with.CreateScope())
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<SourceRegistry>().SportsGameOdds);
    }

    [Fact]
    public void Its_rows_are_kept_from_visitors_and_its_account_is_its_own()
    {
        Assert.Equal(SourceLicence.PersonalUse, SourceLicences.For(SportsGameOddsAdapter.SourceKey));
        Assert.Equal([SportsGameOddsAdapter.SourceKey], SourceAccounts.Sharing(SportsGameOddsAdapter.SourceKey));
        Assert.DoesNotContain(SportsGameOddsAdapter.SourceKey, SourceAccounts.Sharing(SourceAccounts.TheOddsApi));
    }
}

/// <summary>SportsGameOdds' objects: what a pull may spend, and what it says first (ADR 0033).</summary>
public class ObjectAllowanceTests
{
    private static ObjectAllowance Standing(int spent) => new(Budget: 2500, Spent: spent, Reserve: 250, PerPull: 50, WindowDays: 30);

    [Fact]
    public void A_pull_is_capped_at_its_per_pull_ceiling()
    {
        Assert.Equal(50, Standing(0).Ceiling);
        Assert.Equal(CreditCheck.Go, Standing(0).Check(16));
    }

    [Fact]
    public void Near_the_reserve_a_pull_is_cut_to_what_is_left_above_it_and_asks_first()
    {
        var standing = Standing(2220);

        Assert.Equal(30, standing.Ceiling);
        var check = standing.Check(45);
        Assert.True(check.Allowed);
        Assert.Contains("stops at 30 objects", check.Question);
    }

    [Fact]
    public void At_the_reserve_nothing_runs()
    {
        foreach (var spent in new[] { 2250, 2400, 2600 })
        {
            var check = Standing(spent).Check(1);
            Assert.False(check.Allowed);
            Assert.Contains("held in reserve", check.Refusal);
            Assert.Equal(0, Standing(spent).Ceiling);
        }
    }

    [Fact]
    public void The_price_is_stated_in_objects()
        => Assert.Equal("16 objects · 2,484 left", Standing(16).Price(16));
}
