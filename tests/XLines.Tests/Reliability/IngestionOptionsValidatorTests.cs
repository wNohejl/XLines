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

    private static IOptions<IngestionOptions> Bound(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXLinesIngestion(configuration);
        return services.BuildServiceProvider().GetRequiredService<IOptions<IngestionOptions>>();
    }

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
        Assert.Empty(IngestionOptionsValidator.SeasonFailures(options));
        Assert.Equal(["mlb", "nba", "nfl", "ufc"], options.Sports);
    }

    [Fact]
    public void TheDefaultsPass()
        => Assert.Empty(IngestionOptionsValidator.Failures(new IngestionOptions()));

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
    public void ALeagueWithoutASeasonStartIsNamedWhicheverListNamesIt()
    {
        var options = Valid();
        options.Sports = ["mlb", "nfl", "nba"];
        options.Backfill.Sports = ["mlb", "ufc"];

        var failures = IngestionOptionsValidator.SeasonFailures(options).ToList();

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Contains("'nba'", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("'ufc'", StringComparison.Ordinal));
    }

    [Fact]
    public void ALeagueWithoutASeasonStartDoesNotFailTheOptions()
    {
        // The repository's rule, not the code's: a host naming a league with no season start still starts.
        var options = Valid();
        options.Sports = ["nba"];

        Assert.True(new IngestionOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void ASeasonStartIsFoundWhateverItsCase()
    {
        var options = Valid();
        options.Sports = ["MLB", "nfl"];

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
