using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.CrossReference;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace XLines.Web.Services;

/// <summary>
/// What the Players window reads and does: the enabled leagues, the players matching a league and
/// a search with how many games each has a line in, and the operator's Sync stats. A seam so the
/// window holds no DbContext (architecture review 2026-11-27 §1).
/// </summary>
public interface IPlayersData
{
    /// <param name="withSports">False once the picker has its leagues, so they are not read again.</param>
    /// <param name="limit">The most players shown; one more is read to say whether there were more.</param>
    Task<PlayersRead> ReadAsync(bool withSports, int? sportId, string search, int limit, CancellationToken ct = default);

    /// <summary>Pulls yesterday's box scores from every stats source but the scores-only one; returns the rows written.</summary>
    Task<int> SyncStatsAsync();
}

/// <summary>A player with the count of games they have a line in, to the reader's tier.</summary>
public sealed record PlayerGames(Player Player, int Games);

/// <summary>One read of the Players window. <see cref="Sports"/> is null when it was not asked for.</summary>
public sealed record PlayersRead(IReadOnlyList<Sport>? Sports, IReadOnlyList<PlayerGames> Rows, bool Capped);

/// <summary>The window's reads, exactly as the window made them before.</summary>
public sealed class PlayersData(
    IDbContextFactory<XLinesDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> ingestionOptions,
    IDataTier tier) : IPlayersData
{
    public async Task<PlayersRead> ReadAsync(bool withSports, int? sportId, string search, int limit,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sports = withSports
            ? await db.Sports.Where(s => s.Enabled).OrderBy(s => s.Name).ToListAsync(ct)
            : null;

        var query = db.Players.AsNoTracking()
            .Include(p => p.Sport).Include(p => p.Team)
            .Where(p => p.Sport!.Enabled);

        if (sportId is { } id)
            query = query.Where(p => p.SportId == id);

        // Every word must match the name or the team, with LIKE's own characters escaped, the
        // same rule the command palette uses.
        foreach (var pattern in SearchService.Patterns(search))
        {
            query = query.Where(p => EF.Functions.ILike(p.FullName, pattern, @"\")
                                     || (p.Team != null && (EF.Functions.ILike(p.Team.Name, pattern, @"\")
                                                            || EF.Functions.ILike(p.Team.Abbrev, pattern, @"\"))));
        }

        var players = await query.OrderBy(p => p.FullName).Take(limit + 1).ToListAsync(ct);
        var capped = players.Count > limit;
        players = players.Take(limit).ToList();

        var ids = players.Select(p => p.Id).ToList();

        // The lines this reader may see (ADR 0024): a visitor counts a displayable source's alone.
        var counts = await db.PlayerGameStats.FromVisible(await db.VisibleSourceIdsAsync(tier, ct))
            .Where(s => ids.Contains(s.PlayerId))
            .GroupBy(s => s.PlayerId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return new PlayersRead(sports, players.Select(p => new PlayerGames(p, counts.GetValueOrDefault(p.Id, 0))).ToList(), capped);
    }

    public async Task<int> SyncStatsAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var stats = scope.ServiceProvider.GetRequiredService<StatsIngestionService>();

        var yesterday = LeagueClock.Today().AddDays(-1);
        var rows = 0;

        // Box scores are ESPN's: the default source (ADR 0026) has none, and asking it would only
        // write an empty run under a metered row. With ESPN on this is every stats source, as before.
        foreach (var source in registry.StatsSources.Where(s => s is not IScoreSource))
        {
            foreach (var sport in ingestionOptions.Value.EffectiveSports)
            {
                var outcome = await stats.IngestAsync(
                    source, sport, "stats:manual", yesterday, CancellationToken.None);
                rows += outcome.RowsIngested;
            }
        }

        return rows;
    }
}
