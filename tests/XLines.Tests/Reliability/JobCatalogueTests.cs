using XLines.Data;
using XLines.Ingestion;
using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Reliability;
using XLines.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace XLines.Tests.Reliability;

/// <summary>
/// The job catalogue (<see cref="IngestionJobs"/>) over its handlers: every key the menu lists, on
/// every host shape, is run by exactly one handler — so no key can be listed and then refused as
/// unknown, and no two handlers can both claim one — and a key no handler runs is refused as it
/// always was, by name.
/// </summary>
[Collection(PostgresCollection.Name)]
public class JobCatalogueTests(PostgresFixture fixture)
{
    private async Task<string> FreshDatabaseAsync()
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "catalogue_" + Guid.NewGuid().ToString("N")[..12]
        }.ConnectionString;

        await using var db = new XLinesDbContext(new DbContextOptionsBuilder<XLinesDbContext>().UseNpgsql(connection).Options);
        await new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();
        return connection;
    }

    /// <summary>A host as Program.cs builds one, every provider's client answering an empty list.</summary>
    private static ServiceProvider Host(string connection, bool espn, string? oddsKey, string? sgoKey)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Ingestion:Sports:0"] = "mlb",
            ["Ingestion:Sports:1"] = "nba",
            ["Ingestion:Sports:2"] = "nfl",
            ["Ingestion:Sports:3"] = "ufc",
            ["Ingestion:Espn:Enabled"] = espn ? "true" : "false",
            ["Ingestion:MlbStatsApi:Enabled"] = "false",
            ["Ingestion:Gamecast:Enabled"] = "false",
            ["Ingestion:TheOddsApi:Enabled"] = oddsKey is null ? "false" : "true",
            ["Ingestion:TheOddsApi:ApiKey"] = oddsKey ?? "",
            ["Ingestion:SportsGameOdds:ApiKey"] = sgoKey ?? ""
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<XLinesDbContext>(o => o.UseNpgsql(connection));
        services.AddScoped<BudgetCalculator>();
        services.AddScoped(sp => new DatabaseInitializer(sp.GetRequiredService<XLinesDbContext>(), NullLogger<DatabaseInitializer>.Instance));
        services.AddXLinesIngestion(configuration);
        services.AddRuntimeOddsKey();

        var wire = new StubHttp("[]");
        services.AddHttpClient<TheOddsApiAdapter>().ConfigurePrimaryHttpMessageHandler(() => wire);
        if (services.Any(d => d.ServiceType == typeof(TheOddsApiScheduleAdapter)))
            services.AddHttpClient<TheOddsApiScheduleAdapter>().ConfigurePrimaryHttpMessageHandler(() => wire);
        if (espn)
            services.AddHttpClient<EspnStatsAdapter>().ConfigurePrimaryHttpMessageHandler(() => wire);
        if (sgoKey is not null)
            services.AddHttpClient<SportsGameOddsAdapter>().ConfigurePrimaryHttpMessageHandler(() => wire);

        return services.BuildServiceProvider();
    }

    public static TheoryData<bool, string?, string?> Hosts => new()
    {
        { true, "test-key", null },        // the owner's desk: ESPN on, a key
        { true, "test-key", "sgo-key" },   // and the second source's key
        { false, "test-key", "sgo-key" },  // the default source on both keys
        { false, null, null }              // nothing: every pull asks for a key
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Every_key_the_catalogue_describes_resolves_to_exactly_one_handler(bool espn, string? oddsKey, string? sgoKey)
    {
        var connection = await FreshDatabaseAsync();
        await using var host = Host(connection, espn, oddsKey, sgoKey);
        var jobs = host.GetRequiredService<IngestionJobs>();

        var described = (await jobs.DescribeAsync()).Select(j => j.Key).ToList();

        Assert.NotEmpty(described);
        Assert.Equal(described.Count, described.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(IngestionJobs.Settle, described);
        Assert.Contains(IngestionJobs.OddsLinesPrefix + "mlb", described);
        if (sgoKey is not null)
            Assert.Contains(IngestionJobs.SgoLinesPrefix + "mlb", described);

        foreach (var key in described)
            Assert.True(jobs.Handlers.Count(h => h.Matches(key)) == 1, $"'{key}' is matched by {jobs.Handlers.Count(h => h.Matches(key))} handlers.");
    }

    /// <summary>The keys the menu never lists — the scheduler's, and the command line's — are each one handler's too.</summary>
    [Theory]
    [InlineData(IngestionJobs.OddsLines)]
    [InlineData(IngestionJobs.OddsScores)]
    [InlineData(IngestionJobs.EspnRange)]
    [InlineData(JournalTransfer.ExportJob)]
    [InlineData(JournalTransfer.ImportJob)]
    [InlineData(IngestionJobs.EspnSlate)]
    [InlineData(IngestionJobs.EspnLive)]
    [InlineData(IngestionJobs.EspnResults)]
    [InlineData(IngestionJobs.EspnClosingLines)]
    [InlineData(IngestionJobs.EspnFighters)]
    [InlineData(IngestionJobs.EspnGamecastBackport)]
    [InlineData(IngestionJobs.OddsFixtures)]
    public void Every_named_key_resolves_to_exactly_one_handler(string key)
    {
        var jobs = Bare().GetRequiredService<IngestionJobs>();

        Assert.Single(jobs.Handlers, h => h.Matches(key));
    }

    [Theory]
    [InlineData("odds:nothing")]
    [InlineData("settle:now")]
    [InlineData("")]
    public async Task An_unknown_key_is_refused_by_name_and_no_handler_claims_it(string key)
    {
        var jobs = Bare().GetRequiredService<IngestionJobs>();

        Assert.Null(jobs.HandlerFor(key));

        var outcome = await jobs.RunAsync(key);

        Assert.False(outcome.Succeeded);
        Assert.Equal(key, outcome.Key);
        Assert.Equal(0, outcome.Rows);
        Assert.Equal(0, outcome.Failures);
        Assert.Equal($"Unknown job '{key}'.", outcome.Detail);
    }

    /// <summary>The catalogue alone, with no database: enough to ask which handler a key goes to.</summary>
    private static ServiceProvider Bare()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new IngestionOptions()));
        services.AddIngestionJobs();
        return services.BuildServiceProvider();
    }
}
