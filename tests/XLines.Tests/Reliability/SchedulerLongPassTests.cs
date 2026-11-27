using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Ingestion.Services.Jobs;
using XLines.Reliability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace XLines.Tests.Reliability;

/// <summary>
/// The scheduler's tick never waits on a long pass (architecture review §3): with a fighter pass
/// that does not finish, a tick still takes the live poll and the closing scan that is due, and the
/// fighter pass is not started a second time while the first is running. Through the real
/// scheduler, planner and unattended line entry on real Postgres; every job body is a recording
/// stand-in, so nothing reaches ESPN or The Odds API.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SchedulerLongPassTests(PostgresFixture fixture)
{
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Answers every key, records each run, and holds the fighter pass until released.</summary>
    private sealed class RecordingJobs : IIngestionJob
    {
        public TaskCompletionSource FightersRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FightersStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<JobRun> Runs { get; } = [];

        public int Count(string key)
        {
            lock (Runs)
                return Runs.Count(r => r.Key == key);
        }

        public bool Matches(string key) => true;

        public ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct) => JobSupport.Nothing();

        public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            lock (Runs)
                Runs.Add(run);

            if (run.Key == IngestionJobs.EspnFighters)
            {
                FightersStarted.TrySetResult();
                await FightersRelease.Task.WaitAsync(ct);
            }

            return new JobOutcome(run.Key, true, 0, 0, null);
        }
    }

    private async Task<string> FreshDatabaseAsync()
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "longpass_" + Guid.NewGuid().ToString("N")[..12]
        }.ConnectionString;

        await using var db = Context(connection);
        await new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();
        return connection;
    }

    private static XLinesDbContext Context(string connection)
        => new(new DbContextOptionsBuilder<XLinesDbContext>().UseNpgsql(connection).Options);

    private static ServiceProvider Host(string connection, RecordingJobs jobs, TimeProvider clock)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ingestion:Sports:0"] = "mlb",
            ["Ingestion:Sports:1"] = "ufc",
            ["Ingestion:Espn:Enabled"] = "true",
            ["Ingestion:MlbStatsApi:Enabled"] = "false",
            ["Ingestion:Gamecast:Enabled"] = "false",
            ["Ingestion:OddsRetention:Enabled"] = "false",
            ["Ingestion:LinePolling:ClosingScan:Scope"] = nameof(ClosingScanScope.All),
            ["Ingestion:TheOddsApi:Enabled"] = "true",
            ["Ingestion:TheOddsApi:ApiKey"] = "test-key"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);

        // Ahead of the real handlers, so it answers every key the scheduler asks for.
        services.AddSingleton<IIngestionJob>(jobs);
        services.AddDbContext<XLinesDbContext>(o => o.UseNpgsql(connection));
        services.AddScoped<BudgetCalculator>();
        services.AddXLinesIngestion(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// An MLB game in play, another starting inside the closing lead, Automatic scanning, and a
    /// month near enough its reserve that the paced cadence is hours long: the only line scan due
    /// is the closing one.
    /// </summary>
    private async Task<string> SeedAsync(DateTimeOffset now)
    {
        var connection = await FreshDatabaseAsync();
        await using var db = Context(connection);

        var mlb = await db.Sports.SingleAsync(s => s.Key == "mlb");
        var teams = new[] { "Texas Rangers", "Toronto Blue Jays", "Boston Red Sox", "New York Yankees" }
            .Select(n => new Team { SportId = mlb.Id, Name = n, Abbrev = n[..3].ToUpperInvariant() })
            .ToArray();
        db.Teams.AddRange(teams);
        await db.SaveChangesAsync();

        db.Games.AddRange(
            new Game
            {
                SportId = mlb.Id, HomeTeamId = teams[0].Id, AwayTeamId = teams[1].Id,
                StartsAt = now.AddMinutes(-30), SeasonYear = 2026, Status = GameStatus.Live
            },
            new Game
            {
                SportId = mlb.Id, HomeTeamId = teams[2].Id, AwayTeamId = teams[3].Id,
                StartsAt = now.AddMinutes(5), SeasonYear = 2026, Status = GameStatus.Scheduled
            });

        var oddsApi = await db.Sources.Where(s => s.Key == SourceAccounts.TheOddsApi).Select(s => s.Id).SingleAsync();

        db.IngestionRuns.AddRange(
            // The month: 390 of 500 spent leaves a little above the 100 held back.
            new IngestionRun
            {
                SourceId = oddsApi, JobKey = "odds:lines", StartedAt = now.AddDays(-2), FinishedAt = now.AddDays(-2),
                Status = RunStatus.Success, RequestsMade = 1, CreditsSpent = 388
            },
            // The league's last paced scan, before the closing lead began.
            new IngestionRun
            {
                SourceId = oddsApi, JobKey = IngestionJobs.OddsLinesPrefix + "mlb", StartedAt = now.AddMinutes(-7),
                FinishedAt = now.AddMinutes(-7), Status = RunStatus.Success, RequestsMade = 1, CreditsSpent = 2
            });

        await db.SaveChangesAsync();
        await db.SetSettingAsync(AppSetting.LinePolling, AppSetting.LinePollingScheduled);
        await db.SaveChangesAsync();
        return connection;
    }

    private static IngestionScheduler Scheduler(IServiceProvider host, TimeProvider clock)
        => new(host.GetRequiredService<IServiceScopeFactory>(), host.GetRequiredService<IOptions<IngestionOptions>>(),
            NullLogger<IngestionScheduler>.Instance, clock);

    [Fact]
    public async Task A_fighter_pass_that_has_not_finished_does_not_hold_the_live_poll_or_the_closing_scan()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var connection = await SeedAsync(clock.Now);
        var jobs = new RecordingJobs();
        await using var host = Host(connection, jobs, clock);
        var scheduler = Scheduler(host, clock);

        // The tick returns although the fighter pass it started is still running.
        await scheduler.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await jobs.FightersStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(scheduler.LongPasses.IsCompleted);
        Assert.Equal(1, jobs.Count(IngestionJobs.EspnLive));

        lock (jobs.Runs)
            Assert.Contains(jobs.Runs, r => r.Key == IngestionJobs.OddsLinesPrefix + "mlb" && r.ClosingScanDay is not null);

        jobs.FightersRelease.SetResult();
        await scheduler.LongPasses.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_fighter_pass_still_running_is_not_started_again_and_the_next_tick_still_polls_live()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var connection = await SeedAsync(clock.Now);
        var jobs = new RecordingJobs();
        await using var host = Host(connection, jobs, clock);
        var scheduler = Scheduler(host, clock);

        await scheduler.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await jobs.FightersStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var first = scheduler.LongPasses;

        clock.Now += TimeSpan.FromMinutes(1);
        await scheduler.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Same(first, scheduler.LongPasses);
        Assert.Equal(1, jobs.Count(IngestionJobs.EspnFighters));
        Assert.Equal(2, jobs.Count(IngestionJobs.EspnLive));

        // Finished, it is marked done: the next tick inside the hour does not take it again.
        jobs.FightersRelease.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        clock.Now += TimeSpan.FromMinutes(1);
        await scheduler.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, jobs.Count(IngestionJobs.EspnFighters));
        Assert.Equal(3, jobs.Count(IngestionJobs.EspnLive));
    }
}
