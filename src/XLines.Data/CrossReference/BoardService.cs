using XLines.Core.Contracts;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace XLines.Data.CrossReference;

/// <summary>
/// The board: today's slate with the best number available on each market, and which book has it.
///
/// <para>
/// This is the screen the product exists for. Four books disagree, the disagreement is money,
/// and the job is to show where it is without making anyone read four price tables. So each
/// market shows one number — the best one — the book holding it, and how far the rest of the
/// market sits behind. A market where the books agree should look flat; one worth shopping
/// should look wide.
/// </para>
///
/// <para>
/// Read live from the scan tier. Nothing here is precomputed, because "best price" is only
/// true until the next scan and a cached board is a board that lies quietly.
/// </para>
/// </summary>
public class BoardService(XLinesDbContext db, IDataTier? tier = null, TimeProvider? clock = null)
{
    /// <summary>Who is reading, by tier (ADR 0024). Unregistered means a desk with one person at it.</summary>
    private IDataTier Tier { get; } = tier ?? FullDataTier.Instance;

    /// <summary>The one clock this reader reads; the system's unless a host or test passes another.</summary>
    private TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    /// <summary>
    /// Games inside the window, with the best available price on each market.
    /// </summary>
    /// <param name="sportKey">One league, or null for every one in play.</param>
    /// <param name="window">How far ahead to look.</param>
    /// <param name="lookback">
    /// How far <i>back</i> to look. Games already under way or finished belong on the board:
    /// they carry a score and a closing line, and a surface that drops them at first pitch is
    /// one an operator has to leave in order to see how the day went.
    /// </param>
    public async Task<IReadOnlyList<BoardRow>> GetAsync(
        string? sportKey,
        TimeSpan window,
        TimeSpan? lookback = null,
        CancellationToken ct = default)
    {
        var now = Clock.GetUtcNow();
        var horizon = now + window;
        var floor = now - (lookback ?? DefaultLookback);

        var games = await db.Games
            .Include(g => g.Sport)
            .Include(g => g.HomeTeam)
            .Include(g => g.AwayTeam)
            .Where(g => g.StartsAt >= floor && g.StartsAt <= horizon)
            .Where(g => sportKey == null || g.Sport!.Key == sportKey)
            .OrderBy(g => g.StartsAt)
            .AsNoTracking()
            .ToListAsync(ct);

        if (games.Count == 0)
            return [];

        // ESPN's state of play and MLB's probables are columns, not rows: blanked for a visitor
        // to a shared desk (ADR 0024), unchanged for everyone else.
        foreach (var g in games)
            g.ForTier(Tier);

        var gameIds = games.Select(g => g.Id).ToList();

        // What this reader may be shown (ADR 0024): every source on a local desk, the displayable
        // ones for a visitor to a shared one. A personal-use close left out leaves the game with
        // no close, and the row says "no fair close" as it would for a game nobody priced.
        var visible = await db.VisibleSourceIdsAsync(Tier, ct);

        var byGame = await LatestByGameAsync(gameIds, visible, ct);

        // Anything the scan tier no longer holds is asked of the permanent record. That is the
        // whole point of promotion (ADR 0010): the close outlives the stream, so a game in play
        // still has a number even though its scans are gone.
        var missing = gameIds.Where(id => !byGame.ContainsKey(id)).ToList();

        var closesByGame = missing.Count == 0 ? [] : await ClosesByGameAsync(missing, visible, ct);

        // Only asked for the leagues with a row that will have to explain itself. It used to be
        // the newest scan among the window's own games, so a slate nobody had priced yet — the
        // morning after a pull, or any day under manual polling — told every row that no scan
        // had ever run, on a desk with weeks of scans behind it.
        var unexplained = games
            .Where(g => !byGame.ContainsKey(g.Id) && !closesByGame.ContainsKey(g.Id))
            .Select(g => g.SportId)
            .Distinct()
            .ToList();

        var lastPull = unexplained.Count == 0
            ? []
            : await LastPullBySportAsync(
                games.Where(g => unexplained.Contains(g.SportId)).Select(g => g.Sport!).DistinctBy(s => s.Id).ToList(),
                ct);

        var (passing, injuries) = await PassingLinesAsync(games, now, visible, ct);

        // A fight's card and result (ADR 0021), and its place on the card: bouts that share a
        // start are listed main event first, as a card is billed.
        var bouts = await db.Bouts.AsNoTracking().Where(b => gameIds.Contains(b.GameId)).ToDictionaryAsync(b => b.GameId, ct);

        // The card is ESPN's alone, so a visitor's bout says only that the game is a fight.
        foreach (var bout in bouts.Values)
            bout.ForTier(Tier);

        return games
            .OrderBy(g => g.StartsAt)
            .ThenBy(g => bouts.GetValueOrDefault(g.Id)?.MatchNumber ?? 0)
            .Select(game => BoardComposer.Compose(
            game,
            byGame.GetValueOrDefault(game.Id),
            closesByGame.GetValueOrDefault(game.Id),
            lastPull.TryGetValue(game.SportId, out var at) ? at : null,
            now) with
            {
                Bout = bouts.GetValueOrDefault(game.Id)
            })
            .Select(row =>
            {
                // The report is about the next game, not the last: a game under way or finished
                // was started by whoever started it, whatever the club said of him since.
                var report = row.Game.StartsAt > now ? injuries : NoInjuries;
                var away = Starters.Quarterback(passing, report, row.Game.AwayTeamId, row.Game.StartsAt, row.Game.Id);
                var home = Starters.Quarterback(passing, report, row.Game.HomeTeamId, row.Game.StartsAt, row.Game.Id);

                return row with
                {
                    AwayQuarterback = away.Name,
                    AwayQuarterbackBasis = away.Basis,
                    HomeQuarterback = home.Name,
                    HomeQuarterbackBasis = home.Basis
                };
            }).ToList();
    }

    /// <summary>
    /// Every passing line the slate's NFL clubs recorded before their games, for
    /// <see cref="Starters.Quarterback"/>, and the injury report's word on each passer in them. A
    /// handful of rows a club a week. Reaches back over an off-season, so a week-one game still
    /// finds the quarterback who ended last season. Empty for a slate with no NFL game, which asks
    /// nothing of the database.
    /// </summary>
    private async Task<(List<PassingLine> Lines, Dictionary<string, (string Status, string? Note)> Injuries)> PassingLinesAsync(
        List<Game> games, DateTimeOffset now, int[]? visible, CancellationToken ct)
    {
        var nfl = games.Where(g => string.Equals(g.Sport?.Key, "nfl", StringComparison.OrdinalIgnoreCase)).ToList();

        if (nfl.Count == 0)
            return ([], []);

        var teams = nfl.SelectMany(g => new[] { g.HomeTeamId, g.AwayTeamId }).Distinct().ToList();
        var from = nfl.Min(g => g.StartsAt) - TimeSpan.FromDays(240);
        var to = nfl.Max(g => g.StartsAt);

        // The league the slate's NFL rows already name. Without it the jsonb test below ran over
        // every league's box scores for 240 days — a baseball season of lines — to find a
        // handful of passers, since only the clubs were filtered and that after the join.
        var sports = nfl.Select(g => g.SportId).Distinct().ToList();

        var rows = await db.PlayerGameStats.FromVisible(visible)
            // Through the slate's own games, not just before them: a game under way or over is
            // read from its own box score (Starters.Quarterback, ThisGameBasis).
            .Where(s => sports.Contains(s.Game!.SportId))
            .Where(s => s.Game!.StartsAt >= from && s.Game.StartsAt <= to)
            .Where(s => EF.Functions.JsonExists(s.StatLine, "C/ATT"))
            .Select(s => new
            {
                // The side the box score put the player on; a line stored before that was
                // recorded falls back to the player's current club.
                TeamId = s.TeamId ?? s.Player!.TeamId,
                s.GameId,
                s.Game!.StartsAt,
                s.Game.SeasonYear,
                s.Player!.FullName,
                s.Player.InjuryStatus,
                s.Player.InjuryNote,
                s.Player.InjuryUpdatedAt,
                s.StatLine
            })
            .Where(s => s.TeamId != null && teams.Contains(s.TeamId.Value))
            .AsNoTracking()
            .ToListAsync(ct);

        var lines = rows
            .Select(r => new PassingLine(r.TeamId!.Value, r.GameId, r.StartsAt, r.FullName, r.StatLine, r.SeasonYear))
            .ToList();

        // The injury report is ESPN's (ADR 0024): a visitor's starter is read from the lines
        // alone, as if no report named anyone.
        var injuries = Tier.CanSeePersonalUseSources
            ? CurrentInjuries(rows.Select(r => (r.FullName, r.InjuryStatus, r.InjuryNote, r.InjuryUpdatedAt)), now)
            : new Dictionary<string, (string Status, string? Note)>(StringComparer.OrdinalIgnoreCase);

        return (lines, injuries);
    }

    /// <summary>No report: what a game already started is read with, since it was started by whoever started it.</summary>
    private static readonly Dictionary<string, (string Status, string? Note)> NoInjuries = new();

    /// <summary>The injury report's current word on each named player: <see cref="BoardComposer.CurrentInjuries"/>.</summary>
    public static Dictionary<string, (string Status, string? Note)> CurrentInjuries(
        IEnumerable<(string FullName, string? Status, string? Note, DateTimeOffset? UpdatedAt)> players,
        DateTimeOffset now)
        => BoardComposer.CurrentInjuries(players, now);

    /// <summary>
    /// When each league was last priced: its newest scan, or its last successful lines pull,
    /// whichever is later.
    ///
    /// <para>
    /// The scans alone cannot answer it. Promotion deletes a game's scans at first pitch (ADR
    /// 0010), so once every game a manual pull priced has started, the league has no scans left
    /// and looked as though it had never been priced at all. The run log keeps the pull. Only a
    /// league's own pull counts (<c>odds:lines:mlb</c>): the all-leagues job does not record
    /// which leagues it reached, and it runs under automatic polling, which keeps the upcoming
    /// slate in the scan tier anyway.
    /// </para>
    /// </summary>
    private async Task<Dictionary<int, DateTimeOffset>> LastPullBySportAsync(List<Sport> sports, CancellationToken ct)
    {
        var ids = sports.Select(s => s.Id).ToList();

        var scans = await db.OddsSnapshots
            .Where(s => ids.Contains(s.Game!.SportId))
            .GroupBy(s => s.Game!.SportId)
            .Select(g => new { SportId = g.Key, At = g.Max(s => s.CapturedAt) })
            .ToDictionaryAsync(x => x.SportId, x => x.At, ct);

        var jobs = sports.ToDictionary(s => OddsLinesJobPrefix + s.Key, s => s.Id);
        var jobKeys = jobs.Keys.ToList();

        var pulls = await db.IngestionRuns
            .Where(r => jobKeys.Contains(r.JobKey) && (r.Status == RunStatus.Success || r.Status == RunStatus.Partial))
            .GroupBy(r => r.JobKey)
            .Select(g => new { JobKey = g.Key, At = g.Max(r => r.StartedAt) })
            .ToListAsync(ct);

        foreach (var pull in pulls)
        {
            var sportId = jobs[pull.JobKey];
            if (!scans.TryGetValue(sportId, out var scanned) || pull.At > scanned)
                scans[sportId] = pull.At;
        }

        return scans;
    }

    /// <summary>
    /// The job key of a one-league lines pull, less the league. Mirrors
    /// <c>IngestionJobs.OddsLinesPrefix</c>, which this project cannot reference.
    /// </summary>
    public const string OddsLinesJobPrefix = "odds:lines:";

    /// <summary>A closing line with the kind of source that wrote it.</summary>
    private sealed record ClosedQuote(ClosingLine Line, SourceKind Kind);

    /// <summary>
    /// Newest observation per game, book, market and outcome. The scan tier is append-only, so
    /// "current" is the latest row rather than a column.
    /// </summary>
    private async Task<Dictionary<int, List<BoardQuote>>> LatestByGameAsync(List<int> gameIds, int[]? visible, CancellationToken ct)
        => (await db.OddsSnapshots.FromVisible(visible)
                .Where(s => gameIds.Contains(s.GameId))
                .GroupBy(s => new { s.GameId, s.Book, s.Market, s.Outcome })
                .Select(g => g.OrderByDescending(s => s.CapturedAt).First())
                .AsNoTracking()
                .ToListAsync(ct))
            .GroupBy(s => s.GameId)
            .ToDictionary(g => g.Key, g => g.Select(BoardQuote.From).ToList());

    /// <summary>
    /// The close per game, read from the book market where there is one (<see cref="MarketFirstClose"/>):
    /// otherwise the reference would count as one more book and could win "best".
    /// </summary>
    private async Task<Dictionary<int, List<BoardQuote>>> ClosesByGameAsync(List<int> gameIds, int[]? visible, CancellationToken ct)
        => MarketFirstClose.PerGame(
                await db.ClosingLines.FromVisible(visible)
                    .Where(c => gameIds.Contains(c.GameId))
                    .Select(c => new ClosedQuote(c, c.Source!.Kind))
                    .AsNoTracking()
                    .ToListAsync(ct),
                c => c.Line.GameId, c => c.Kind)
            .ToDictionary(g => g.Key, g => g.Value.Select(c => BoardQuote.From(c.Line)).ToList());

    /// <summary>
    /// Three hours back by default, which is roughly a game.
    ///
    /// The board used to stop here and it was the wrong ceiling for a surface that also has to
    /// answer "how did tonight go" — but it stays the default so a caller that wants only the
    /// live market gets it without saying so.
    /// </summary>
    public static readonly TimeSpan DefaultLookback = TimeSpan.FromHours(3);

    /// <summary>
    /// One game's row, for a view opened against a game id rather than handed a row.
    ///
    /// The board's actions can open either as a desk window or as a floating panel, and a
    /// window is reconstructed from its parameters on every render — so the views take an id
    /// and load, rather than closing over a row that a window cannot carry.
    /// </summary>
    public async Task<BoardRow?> GetRowAsync(int gameId, CancellationToken ct = default)
    {
        var game = await db.Games
            .Include(g => g.Sport)
            .Include(g => g.HomeTeam)
            .Include(g => g.AwayTeam)
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == gameId, ct);

        if (game is null)
            return null;

        game.ForTier(Tier);

        var visible = await db.VisibleSourceIdsAsync(Tier, ct);

        var live = (await LatestByGameAsync([gameId], visible, ct)).GetValueOrDefault(gameId) ?? [];

        var closing = live.Count > 0
            ? []
            : (await ClosesByGameAsync([gameId], visible, ct)).GetValueOrDefault(gameId) ?? [];

        // Only asked when the row is going to have to explain itself, because the answer is
        // only used to tell "the feed has never run" apart from "the feed skipped this
        // fixture". Passing null unconditionally made every unpriced game claim no scan had
        // ever run — which reads as a broken platform on a desk holding a full slate of prices.
        DateTimeOffset? scanned = live.Count > 0 || closing.Count > 0
            ? null
            : (await LastPullBySportAsync([game.Sport!], ct)).TryGetValue(game.SportId, out var at) ? at : null;

        return BoardComposer.Compose(game, live, closing, scanned, Clock.GetUtcNow()) with
        {
            Bout = (await db.Bouts.AsNoTracking().FirstOrDefaultAsync(b => b.GameId == gameId, ct)).ForTier(Tier)
        };
    }
}
