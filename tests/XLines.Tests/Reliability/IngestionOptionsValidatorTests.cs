using System.Text.Json;
using XLines.Ingestion;
using XLines.Ingestion.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace XLines.Tests.Reliability;

/// <summary>
/// The <c>Ingestion</c> section checked for what the services assume (<see cref="IngestionOptionsValidator"/>),
/// and the two shipped configurations shown to pass it, as bound by the hosts themselves.
/// </summary>
public class IngestionOptionsValidatorTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XLines.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("XLines.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// A JSON settings file as the configuration keys its JSON provider makes of it (<c>A:B:0</c>):
    /// this project carries no JSON configuration provider, and the binder reads the keys alike.
    /// </summary>
    private static Dictionary<string, string?> Flatten(string json)
    {
        var keys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        void Walk(JsonElement node, string path)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in node.EnumerateObject())
                        Walk(property.Value, path.Length == 0 ? property.Name : $"{path}:{property.Name}");
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in node.EnumerateArray())
                        Walk(item, $"{path}:{i++}");
                    break;
                case JsonValueKind.Null:
                    keys[path] = null;
                    break;
                default:
                    keys[path] = node.ValueKind == JsonValueKind.String ? node.GetString() : node.GetRawText();
                    break;
            }
        }

        Walk(doc.RootElement, "");
        return keys;
    }

    private static ServiceProvider Provider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXLinesIngestion(configuration);
        return services.BuildServiceProvider();
    }

    private static IOptions<IngestionOptions> Bound(IConfiguration configuration)
        => Provider(configuration).GetRequiredService<IOptions<IngestionOptions>>();

    private static IngestionOptions Valid() => new()
    {
        Sports = ["mlb", "nfl"],
        Backfill = new BackfillOptions
        {
            Sports = ["mlb"],
            Seasons = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase)
            {
                ["mlb"] = new(2026, 3, 25),
                ["nfl"] = new(2025, 9, 4)
            }
        }
    };

    [Theory]
    [InlineData("src/XLines.Web/appsettings.json")]
    [InlineData("src/XLines.Worker/appsettings.json")]
    public void TheShippedConfigurationPasses(string file)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Flatten(File.ReadAllText(Path.Combine(Root(), file))))
            .Build();

        // Bound as the host binds it: the section, its normalising pass, then the validator.
        var options = Bound(configuration).Value;

        Assert.Empty(IngestionOptionsValidator.Failures(options));
        Assert.Equal(["mlb", "nba", "nfl", "ufc"], options.Sports);
        Assert.Equal(["mlb", "nba", "nfl", "ufc"], IngestionOptionsValidator.BackfillLeagues(options));
    }

    [Theory]
    [InlineData("src/XLines.Web/appsettings.json")]
    [InlineData("src/XLines.Worker/appsettings.json")]
    public void AHostOnTheShippedConfigurationStarts(string file)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Flatten(File.ReadAllText(Path.Combine(Root(), file))))
            .Build();

        using var provider = Provider(configuration);

        // What the host runs before its first hosted service (ValidateOnStart): it does not throw.
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void TheDefaultsPassOnceTheirLeaguesHaveSeasonStarts()
    {
        // Naming no leagues walks the default ones, so a bare section owes each of them a start.
        var options = new IngestionOptions();

        var failures = IngestionOptionsValidator.Failures(options).ToList();
        Assert.Equal(IngestionOptions.DefaultSports.Length, failures.Count);
        Assert.All(IngestionOptions.DefaultSports, league =>
            Assert.Contains(failures, f => f.StartsWith($"Ingestion:Backfill:Seasons:{league} must be set", StringComparison.Ordinal)));

        foreach (var league in IngestionOptions.DefaultSports)
            options.Backfill.Seasons[league] = new DateOnly(2025, 9, 1);

        Assert.Empty(IngestionOptionsValidator.Failures(options));
    }

    [Fact]
    public void AConfigurationWithItsSeasonsPasses()
        => Assert.True(new IngestionOptionsValidator().Validate(null, Valid()).Succeeded);

    [Fact]
    public void AZeroCadenceIsRefusedByItsKey()
    {
        var options = Valid();
        options.TickInterval = TimeSpan.Zero;
        options.Gamecast.Poll = TimeSpan.FromSeconds(-1);

        var failures = IngestionOptionsValidator.Failures(options).ToList();

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.StartsWith("Ingestion:TickInterval must be positive", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Ingestion:Gamecast:Poll must be positive", StringComparison.Ordinal));
    }

    [Fact]
    public void APacingFloorAboveItsCeilingIsRefused()
    {
        var options = Valid();
        options.LinePolling.MinimumInterval = TimeSpan.FromHours(4);
        options.LinePolling.MaximumInterval = TimeSpan.FromHours(3);

        var failure = Assert.Single(IngestionOptionsValidator.Failures(options));
        Assert.StartsWith("Ingestion:LinePolling:MinimumInterval", failure);
    }

    [Fact]
    public void ANegativePauseIsRefusedAndAZeroOneIsNot()
    {
        var options = Valid();
        options.Espn.RequestDelay = TimeSpan.Zero;
        options.Backfill.RequestDelay = TimeSpan.FromMilliseconds(-5);

        var failure = Assert.Single(IngestionOptionsValidator.Failures(options));
        Assert.StartsWith("Ingestion:Backfill:RequestDelay must not be negative", failure);
    }

    [Fact]
    public void AReserveAboveItsAllowanceOrBelowNothingIsRefused()
    {
        var options = Valid();
        options.SportsGameOdds.MonthlyObjects = 100;
        options.SportsGameOdds.ObjectReserve = 250;
        options.LinePolling.MonthlyCreditReserve = -1;

        var failures = IngestionOptionsValidator.Failures(options).ToList();

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.StartsWith("Ingestion:SportsGameOdds:ObjectReserve (250) must not exceed", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Ingestion:LinePolling:MonthlyCreditReserve must not be negative", StringComparison.Ordinal));
    }

    [Fact]
    public void ALeagueTheBackfillNamesWithoutASeasonStartIsRefusedByItsKey()
    {
        var options = Valid();
        options.Backfill.Sports = ["mlb", "ufc"];

        var failure = Assert.Single(IngestionOptionsValidator.Failures(options));
        Assert.StartsWith("Ingestion:Backfill:Seasons:ufc must be set", failure);
        Assert.Contains("'ufc'", failure);
        Assert.False(new IngestionOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void WithNoBackfillListTheLeaguesPolledAreTheLeaguesWalkedAndEachNeedsASeasonStart()
    {
        var options = Valid();
        options.Sports = ["mlb", "nfl", "nba"];
        options.Backfill.Sports = [];

        var failure = Assert.Single(IngestionOptionsValidator.Failures(options));
        Assert.StartsWith("Ingestion:Backfill:Seasons:nba must be set", failure);
    }

    [Fact]
    public void ALeaguePolledButLeftOutOfTheBackfillNeedsNoSeasonStart()
    {
        // No walk takes it, so nothing can reach back for want of its start.
        var options = Valid();
        options.Sports = ["mlb", "nba"];
        options.Backfill.Sports = ["mlb"];

        Assert.True(new IngestionOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHostWithALeagueAndABackfillButNoSeasonStartRefusesToStartNamingTheLeague(bool enabled)
    {
        // Off as well as on: Enabled only starts the walk by itself, and the History window's press
        // starts the same walk. Since and Days are set and are no substitute for a season's start.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:Sports:0"] = "mlb",
                ["Ingestion:Sports:1"] = "nba",
                ["Ingestion:Backfill:Enabled"] = enabled ? "true" : "false",
                ["Ingestion:Backfill:Since"] = "2026-03-01",
                ["Ingestion:Backfill:Days"] = "180",
                ["Ingestion:Backfill:Sports:0"] = "mlb",
                ["Ingestion:Backfill:Sports:1"] = "nba",
                ["Ingestion:Backfill:Seasons:mlb"] = "2026-03-25"
            })
            .Build();

        using var provider = Provider(configuration);

        var refused = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        var failure = Assert.Single(refused.Failures);
        Assert.StartsWith("Ingestion:Backfill:Seasons:nba must be set", failure);
        Assert.Contains("'nba'", failure);
    }

    [Fact]
    public void ASeasonStartIsFoundWhateverItsCase()
    {
        var options = Valid();
        options.Sports = ["MLB", "nfl"];
        options.Backfill.Sports = [];

        Assert.Empty(IngestionOptionsValidator.SeasonFailures(options));
    }

    [Fact]
    public void TheValidatorIsRegisteredWithTheOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ingestion:TickInterval"] = "00:00:00" })
            .Build();

        var refused = Assert.Throws<OptionsValidationException>(() => Bound(configuration).Value);
        Assert.Contains(refused.Failures, f => f.StartsWith("Ingestion:TickInterval", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOptionsAreValidatedWhenTheHostStarts()
    {
        // What a host runs before its first hosted service (ValidateOnStart), here without one.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ingestion:Gamecast:BoxPoll"] = "-00:00:01" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXLinesIngestion(configuration);
        using var provider = services.BuildServiceProvider();

        var refused = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains(refused.Failures, f => f.StartsWith("Ingestion:Gamecast:BoxPoll", StringComparison.Ordinal));
    }
}
