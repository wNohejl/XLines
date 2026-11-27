using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.CrossReference;
using Microsoft.EntityFrameworkCore;

namespace XLines.Web.Services;

/// <summary>
/// What the Player window reads: the player, the seasons they have played and one season's games,
/// whether a game a notice named is theirs, and a game's row to open it by. A seam so the window
/// holds no DbContext (architecture review 2026-11-27 §1).
/// </summary>
public interface IPlayerData
{
    /// <summary>
    /// The player and, for a player found, the season to show — <paramref name="asked"/> while
    /// they played in it, else their newest — and its games. Each read needs the one before it.
    /// </summary>
    Task<PlayerRead> ReadAsync(int playerId, int? asked, int maxGames, CancellationToken ct = default);

    /// <summary>Whether any of these games is the player's, to the reader's tier.</summary>
    Task<bool> PlaysInAnyAsync(int playerId, int[] ids, CancellationToken ct = default);

    /// <summary>A game's Board row, to open the Game window on it.</summary>
    Task<BoardRow?> RowAsync(int gameId);
}

/// <summary>One load of the Player window; the seasons and rows are empty for a player not found.</summary>
public sealed record PlayerRead(
    Player? Player,
    IReadOnlyList<int> Seasons,
    int? SeasonYear,
    IReadOnlyList<PlayerGameRow> Rows);

/// <summary>The window's reads, exactly as the window made them before.</summary>
public sealed class PlayerData(IDbContextFactory<XLinesDbContext> dbFactory, IServiceScopeFactory scopeFactory) : IPlayerData
{
    /// <summary>
    /// The check's query: those of the games, by primary key, that the player's current team played
    /// in or that hold a stat line of theirs. The team is read in the query rather than taken from
    /// the window, so a window whose load failed still asks the right question.
    /// </summary>
    public static IQueryable<Game> OwnGames(XLinesDbContext db, int playerId, int[] ids, int[]? visible = null)
    {
        // Only stat lines from sources this reader may see (ADR 0024), as the window's own reads are.
        var lines = db.PlayerGameStats.FromVisible(visible);

        return db.Games.AsNoTracking()
            .Where(g => ids.Contains(g.Id))
            .Where(g => db.Players.Any(p => p.Id == playerId && p.TeamId != null
                                            && (p.TeamId == g.HomeTeamId || p.TeamId == g.AwayTeamId))
                        || lines.Any(s => s.GameId == g.Id && s.PlayerId == playerId));
    }

    public async Task<bool> PlaysInAnyAsync(int playerId, int[] ids, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var scope = scopeFactory.CreateAsyncScope();

        // Who is reading, by tier; unregistered means a desk with one person at it.
        IDataTier tier = scope.ServiceProvider.GetService<IDataTier>()
            ?? FullDataTier.Instance;

        return await OwnGames(db, playerId, ids, await db.VisibleSourceIdsAsync(tier, ct)).AnyAsync(ct);
    }

    public async Task<PlayerRead> ReadAsync(int playerId, int? asked, int maxGames, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var player = await db.Players
            .Include(p => p.Team)
            .Include(p => p.Sport)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playerId, ct);

        IReadOnlyList<int> seasons = [];
        int? seasonYear = null;
        IReadOnlyList<PlayerGameRow> rows = [];

        if (player is not null)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var log = scope.ServiceProvider.GetRequiredService<GameLogService>();

            seasons = await log.SeasonsForPlayerAsync(playerId, ct);

            seasonYear = SubjectSeasons.Pick(seasons, asked);

            rows = await log.PlayerGameLogAsync(playerId, maxGames, seasonYear, ct);
        }

        return new PlayerRead(player, seasons, seasonYear, rows);
    }

    public async Task<BoardRow?> RowAsync(int gameId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var board = scope.ServiceProvider.GetRequiredService<BoardService>();

        return await board.GetRowAsync(gameId);
    }
}
