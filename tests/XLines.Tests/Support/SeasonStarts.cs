using Microsoft.Extensions.Configuration;

namespace XLines.Tests.Support;

/// <summary>
/// The season starts a real configuration carries (<c>Ingestion:Backfill:Seasons</c>), for a test host
/// built from in-memory settings. The host refuses to start with a league the backfill would walk and
/// no season start for it (<see cref="Ingestion.Configuration.IngestionOptionsValidator"/>), so every
/// such host is given them first and its own settings after, which win where they name the same key.
/// </summary>
public static class SeasonStarts
{
    /// <summary>
    /// The four the shipped appsettings.json files name, and the NHL's 2025–26 opening night for a
    /// host that names no leagues and so walks <see cref="Ingestion.Configuration.IngestionOptions.DefaultSports"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> Settings = new Dictionary<string, string?>
    {
        ["Ingestion:Backfill:Seasons:mlb"] = "2026-03-25",
        ["Ingestion:Backfill:Seasons:nba"] = "2026-10-20",
        ["Ingestion:Backfill:Seasons:nfl"] = "2025-09-04",
        ["Ingestion:Backfill:Seasons:ufc"] = "2024-04-13",
        ["Ingestion:Backfill:Seasons:nhl"] = "2025-10-07"
    };

    /// <summary>Adds <see cref="Settings"/>; call it before the test's own settings so theirs win.</summary>
    public static IConfigurationBuilder AddSeasonStarts(this IConfigurationBuilder builder)
        => builder.AddInMemoryCollection(Settings);
}
