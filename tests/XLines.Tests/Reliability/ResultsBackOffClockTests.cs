using XLines.Tests.Support;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Ingestion.Services.Jobs;
using XLines.Reliability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace XLines.Tests.Reliability;

/// <summary>
/// The results sweep's back-off, decided against the one clock (<see cref="TimeProvider"/>) rather
/// than the wall's: a date swept without healing waits out its retry before it is walked again,
/// and is walked again once the wait is over. Before the clock was injected this could only be
/// seen by waiting the half hour out.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ResultsBackOffClockTests(PostgresFixture fixture)
{
    /// <summary>A clock that stands still until told to move.</summary>
    private sealed class PinnedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_date_swept_without_healing_waits_out_its_retry_on_the_pinned_clock_and_is_swept_again_after_it()
    {
        // Whole seconds, so the start compares the same after a round trip through Postgres.
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var clock = new PinnedClock(start);

        // A database of its own: every game anywhere that never went final is owed, so a shared one
        // would sweep other tests' days alongside this one.
        var connection = await TestDatabases.FreshAsync(fixture.ConnectionString, "backoff");

        await using var db = new XLinesDbContext(new DbContextOptionsBuilder<XLinesDbContext>().UseNpgsql(connection).Options);


        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sport = new Sport { Key = $"backoff-{suffix}", Name = "TEST" };
        db.Sports.Add(sport);
        await db.SaveChangesAsync();

        var home = new Team { SportId = sport.Id, Name = $"Home {suffix}", Abbrev = "HOM" };
        var away = new Team { SportId = sport.Id, Name = $"Away {suffix}", Abbrev = "AWY" };
        db.Teams.AddRange(home, away);
        await db.SaveChangesAsync();

        // Started two days ago and never went final here: owed, and no source can heal it.
        db.Games.Add(new Game
        {
            SportId = sport.Id,
            HomeTeamId = home.Id,
            AwayTeamId = away.Id,
            StartsAt = start.AddDays(-2),
            Status = GameStatus.Scheduled,
            SeasonYear = start.Year
        });
        await db.SaveChangesAsync();

        var options = new IngestionOptions { Sports = [sport.Key] };
        options.GamePasses.ResultsRetry = TimeSpan.FromMinutes(30);

        var services = new ServiceCollection();
        services.AddDbContext<XLinesDbContext>(o => o.UseNpgsql(connection));
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(Options.Create(options));
        services.AddScoped(sp => new BudgetCalculator(sp.GetRequiredService<XLinesDbContext>()));
        services.AddScoped<EntityResolver>();
        services.AddScoped<CreditBudgetGuard>();
        services.AddScoped<StatsIngestionService>();
        services.AddScoped(_ => new SourceRegistry([], []));
        services.AddIngestionJobs();
        await using var host = services.BuildServiceProvider();

        // The job itself: the facade would refuse an ESPN job on a host with no ESPN registered.
        var results = host.GetRequiredService<EspnResultsJob>();
        var day = LeagueClock.DateOf(start.AddDays(-2));

        Assert.Contains(day, await host.GetRequiredService<IngestionJobs>().DatesAwaitingResultsAsync(options.GamePasses.ResultsAfterStart));

        var first = await results.RunAsync(new JobRun(IngestionJobs.EspnResults), CancellationToken.None);
        Assert.Equal("1 day swept, 0 rows.", first.Detail);

        // The first fruitless sweep waits twice the retry: an hour. A minute short of it, nothing is walked.
        clock.Now = start.AddMinutes(59);
        var held = await results.RunAsync(new JobRun(IngestionJobs.EspnResults), CancellationToken.None);
        Assert.Equal("0 days swept, 0 rows, 1 still owed and backing off.", held.Detail);

        // A minute past it, the date is walked again.
        clock.Now = start.AddMinutes(61);
        var again = await results.RunAsync(new JobRun(IngestionJobs.EspnResults), CancellationToken.None);
        Assert.Equal("1 day swept, 0 rows.", again.Detail);
    }
}
