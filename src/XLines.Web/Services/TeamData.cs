using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.CrossReference;
using Microsoft.EntityFrameworkCore;

namespace XLines.Web.Services;

/// <summary>
/// What the Team window reads: the seasons the team has played, the season's roster aggregate and
/// its games, whether a game a notice named is the team's, and a game's row to open it by. A seam so
/// the window holds no DbContext (architecture review 2026-11-27 §1).
/// </summary>
public interface ITeamData
{
    /// <summary>
    /// The season to show — <paramref name="asked"/> while the team played in it, else the newest —
    /// its roster form and its games. The reads share one scope's context, one after another.
    /// </summary>
    Task<TeamRead> ReadAsync(int teamId, int? asked, TimeSpan fallbackWindow, CancellationToken ct = default);

    /// <summary>Whether any of these games is the team's.</summary>
    Task<bool> PlaysInAnyAsync(int teamId, int[] ids, CancellationToken ct = default);

    /// <summary>A game's Board row, to open the Game window on it.</summary>
    Task<BoardRow?> RowAsync(int gameId);
}

/// <summary>One load of the Team window.</summary>
public sealed record TeamRead(
    IReadOnlyList<int> Seasons,
    int? SeasonYear,
    TeamForm? Form,
    IReadOnlyList<TeamGameLogRow> Games);

/// <summary>The window's reads, a scope per call, exactly as the window made them before.</summary>
public sealed class TeamData(IServiceScopeFactory scopeFactory) : ITeamData
{
    /// <summary>The check's query: those of the games that this team played in, by primary key.</summary>
    public static IQueryable<Game> OwnGames(XLinesDbContext db, int teamId, int[] ids)
        => db.Games.AsNoTracking()
            .Where(g => ids.Contains(g.Id) && (g.HomeTeamId == teamId || g.AwayTeamId == teamId));

    public async Task<bool> PlaysInAnyAsync(int teamId, int[] ids, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

        return await OwnGames(db, teamId, ids).AnyAsync(ct);
    }

    public async Task<TeamRead> ReadAsync(int teamId, int? asked, TimeSpan fallbackWindow, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<MatchupCrossReference>();
        var log = scope.ServiceProvider.GetRequiredService<GameLogService>();

        // The seasons this team has played in decide the gate; the newest is the default.
        var seasons = await log.SeasonsForTeamAsync(teamId, ct);

        var seasonYear = SubjectSeasons.Pick(seasons, asked);

        // The roster still comes from the aggregate side — per-player form over a season is
        // exactly what it is for. Only the games are read at game grain.
        var form = await lookup.GetTeamAsync(teamId, fallbackWindow, maxPlayers: 15, seasonYear: seasonYear, ct: ct);

        var games = await log.TeamGameLogAsync(teamId, fallbackWindow, seasonYear, ct);

        return new TeamRead(seasons, seasonYear, form, games);
    }

    public async Task<BoardRow?> RowAsync(int gameId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var board = scope.ServiceProvider.GetRequiredService<BoardService>();

        return await board.GetRowAsync(gameId);
    }
}
