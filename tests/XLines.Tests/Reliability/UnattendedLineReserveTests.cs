using System.Net;
using System.Reflection;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion;
using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Reliability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace XLines.Tests.Reliability;

/// <summary>
/// Unattended line scans never spend into the reserve held for pulls on request (CLAUDE.md, ADR
/// 0026): with the month's remainder at the reserve, neither the scheduler's paced scan nor its
/// closing scan writes a run or makes a request, while an operator's press of the same job still
/// runs. Through the real jobs, a recording wire for The Odds API, and real Postgres.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class UnattendedLineReserveTests(PostgresFixture fixture)
{
    private const string MlbLines = IngestionJobs.OddsLinesPrefix + "mlb";

    private sealed class Wire : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        public IEnumerable<Uri> OddsCalls => Asked.Where(u => u.Host == "api.the-odds-api.com" && u.AbsolutePath.Contains("/odds", StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Asked)
                Asked.Add(request.RequestUri!);

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            response.Headers.Add("x-requests-last", request.RequestUri!.AbsolutePath.Contains("/odds", StringComparison.Ordinal) ? "3" : "0");
            return Task.FromResult(response);
        }
    }

    private async Task<string> FreshDatabaseAsync()
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "linereserve_" + Guid.NewGuid().ToString("N")[..12]
        }.ConnectionString;

        await using var db = Context(connection);
        await new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();
        return connection;
    }

    private static XLinesDbContext Context(string connection)
        => new(new DbContextOptionsBuilder<XLinesDbContext>().UseNpgsql(connection).Options);

    private static ServiceProvider Host(string connection, Wire wire)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ingestion:Sports:0"] = "mlb",
            ["Ingestion:Espn:Enabled"] = "false",
            ["Ingestion:MlbStatsApi:Enabled"] = "false",
            ["Ingestion:Gamecast:Enabled"] = "false",
            ["Ingestion:TheOddsApi:Enabled"] = "true",
            ["Ingestion:TheOddsApi:ApiKey"] = "test-key"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<XLinesDbContext>(o => o.UseNpgsql(connection));
        services.AddScoped<BudgetCalculator>();
        services.AddScoped(sp => new DatabaseInitializer(sp.GetRequiredService<XLinesDbContext>(), NullLogger<DatabaseInitializer>.Instance));
        services.AddXLinesIngestion(configuration);
        services.AddHttpClient<TheOddsApiAdapter>().ConfigurePrimaryHttpMessageHandler(() => wire);
        services.AddHttpClient<TheOddsApiScheduleAdapter>().ConfigurePrimaryHttpMessageHandler(() => wire);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A month spent down to <paramref name="spent"/> credits, scanning Automatic, and an MLB game
    /// two hours out: inside the movement window, and inside no closing lead yet.
    /// </summary>
    private async Task<string> SeedAsync(int spent, bool automatic = true)
    {
        var connection = await FreshDatabaseAsync();
        await using var db = Context(connection);

        var mlb = await db.Sports.SingleAsync(s => s.Key == "mlb");
        var home = new Team { SportId = mlb.Id, Name = "Texas Rangers", Abbrev = "TEX" };
        var away = new Team { SportId = mlb.Id, Name = "Toronto Blue Jays", Abbrev = "TOR" };
        db.Teams.AddRange(home, away);
        await db.SaveChangesAsync();

        db.Games.Add(new Game
        {
            SportId = mlb.Id,
            HomeTeamId = home.Id,
            AwayTeamId = away.Id,
            StartsAt = DateTimeOffset.UtcNow.AddHours(2),
            SeasonYear = 2026,
            Status = GameStatus.Scheduled
        });

        if (spent > 0)
        {
            db.IngestionRuns.Add(new IngestionRun
            {
                SourceId = await db.Sources.Where(s => s.Key == SourceAccounts.TheOddsApi).Select(s => s.Id).SingleAsync(),
                JobKey = "odds:lines",
                StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
                FinishedAt = DateTimeOffset.UtcNow.AddHours(-3),
                Status = RunStatus.Success,
                RequestsMade = 1,
                CreditsSpent = spent
            });
        }

        await db.SaveChangesAsync();
        await db.SetSettingAsync(AppSetting.LinePolling, automatic ? AppSetting.LinePollingScheduled : AppSetting.LinePollingManual);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task<int> LineRunsAsync(string connection)
    {
        await using var db = Context(connection);
        return await db.IngestionRuns.CountAsync(r => r.JobKey == MlbLines);
    }

    private static Task TickAsync(IServiceProvider host)
    {
        var scheduler = new IngestionScheduler(host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<Microsoft.Extensions.Options.IOptions<IngestionOptions>>(), NullLogger<IngestionScheduler>.Instance, TimeProvider.System);

        return (Task)typeof(IngestionScheduler).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [CancellationToken.None])!;
    }

    [Fact]
    public async Task Automatic_with_the_remainder_at_the_reserve_an_unattended_line_scan_writes_no_run_and_makes_no_request()
    {
        // 500 a month, 100 held in reserve: 400 spent leaves exactly the reserve.
        var connection = await SeedAsync(spent: 400);
        var wire = new Wire();
        await using var host = Host(connection, wire);
        var jobs = host.GetRequiredService<IngestionJobs>();

        var paced = await jobs.RunUnattendedLinesAsync("mlb");
        var closing = await jobs.RunUnattendedLinesAsync("mlb", XLines.Core.Analytics.LeagueClock.Today());

        Assert.Contains("Held back for MLB", paced.Detail);
        Assert.Contains("Held back for MLB", closing.Detail);
        Assert.Empty(wire.OddsCalls);
        Assert.Equal(0, await LineRunsAsync(connection));
    }

    [Fact]
    public async Task Automatic_with_the_remainder_at_the_reserve_the_scheduler_plans_no_league_and_its_tick_buys_no_lines()
    {
        var connection = await SeedAsync(spent: 400);
        var wire = new Wire();
        await using var host = Host(connection, wire);

        await using (var scope = host.CreateAsyncScope())
        {
            var plan = await scope.ServiceProvider.GetRequiredService<LinePollPlanner>().PlanAsync([SourceAccounts.TheOddsApi]);
            Assert.Equal(0, plan!.ScansRemainingToday);
            Assert.Empty(plan.Leagues);
        }

        await TickAsync(host);

        Assert.Empty(wire.OddsCalls);
        Assert.Equal(0, await LineRunsAsync(connection));
    }

    [Fact]
    public async Task An_operator_press_at_the_reserve_still_runs()
    {
        var connection = await SeedAsync(spent: 400);
        var wire = new Wire();
        await using var host = Host(connection, wire);

        var outcome = await host.GetRequiredService<IngestionJobs>().RunAsync(MlbLines);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Single(wire.OddsCalls);
        Assert.Equal(1, await LineRunsAsync(connection));
    }

    [Fact]
    public async Task Above_the_reserve_an_unattended_line_scan_runs()
    {
        var connection = await SeedAsync(spent: 0);
        var wire = new Wire();
        await using var host = Host(connection, wire);

        var outcome = await host.GetRequiredService<IngestionJobs>().RunUnattendedLinesAsync("mlb");

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Single(wire.OddsCalls);
        Assert.Equal(1, await LineRunsAsync(connection));
    }

    [Fact]
    public async Task On_request_an_unattended_line_scan_spends_nothing_whatever_is_left()
    {
        var connection = await SeedAsync(spent: 0, automatic: false);
        var wire = new Wire();
        await using var host = Host(connection, wire);

        var outcome = await host.GetRequiredService<IngestionJobs>().RunUnattendedLinesAsync("mlb");

        Assert.Contains("on request only", outcome.Detail);
        Assert.Empty(wire.OddsCalls);
        Assert.Equal(0, await LineRunsAsync(connection));
    }
}
