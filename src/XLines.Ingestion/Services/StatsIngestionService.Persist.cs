using System.Text.Json;
using System.Text.Json.Nodes;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Services;

/// <summary>
/// Writing what a stats fetch returned: its games and bouts, the players who appeared, their stat
/// lines and each game's context, in one save.
/// </summary>
public partial class StatsIngestionService
{
    private async Task<int> PersistAsync(
        Source sourceRow, StatsFetchResult result, string sportKey, Func<CancellationToken, Task<long>> runId,
        bool live, CancellationToken ct)
    {
        var sport = await resolver.ResolveSportAsync(sportKey, ct);
        var rows = 0;

        // Games first: scores arriving here are what make settlement possible.
        var gameMap = new Dictionary<string, Game>();
        rows += await PersistGamesAsync(sport, sourceRow, result.Games, gameMap, ct);

        // Resolve each stat line to a game *before* creating anyone, so that a player only
        // enters the database because they played in a game we can point at.
        //
        // A player who never appears in a box score is a row that will never be read: the
        // cross-reference skips them, the Players panel shows them with nothing, and they cost
        // an index entry each. Any source that reports a roster ahead of the games it played in
        // produces exactly that: every player created, and every stat line then dropped for
        // having nowhere to go.
        var resolved = new List<(CanonicalPlayerStat Stat, Game Game)>();
        Dictionary<string, Game>? knownByExternalId = null;

        foreach (var stat in result.PlayerStats)
        {
            if (!gameMap.TryGetValue(stat.SourceGameId, out var game))
            {
                // The box score references a game this payload did not carry. Load the sport's
                // games once and index them, rather than re-querying per stat line.
                knownByExternalId ??= await KnownByExternalIdAsync(sport, sourceRow, ct);

                if (!knownByExternalId.TryGetValue(stat.SourceGameId, out game))
                    continue;

                gameMap[stat.SourceGameId] = game;
            }

            resolved.Add((stat, game));
        }

        var playerMap = await PersistParticipantsAsync(sport, sourceRow, sportKey, result.Players, resolved, ct);
        rows += playerMap.Count;

        rows += await PersistStatLinesAsync(sport, sourceRow, sportKey, resolved, playerMap, runId, live, ct);

        rows += await PersistClosingLinesAsync(sourceRow, result.Lines, gameMap, ct);

        rows += await PersistContextsAsync(sport, sourceRow, result.Contexts, gameMap, knownByExternalId, ct);

        await db.SaveChangesAsync(ct);
        return rows;
    }

    /// <summary>Each game the fetch carried, resolved into <paramref name="gameMap"/> with its bout where it is a fight.</summary>
    /// <returns>The rows written: one a game, and one a bout written or changed.</returns>
    private async Task<int> PersistGamesAsync(
        Sport sport, Source sourceRow, IReadOnlyList<CanonicalGame> games, Dictionary<string, Game> gameMap,
        CancellationToken ct)
    {
        var rows = 0;

        foreach (var canonical in games)
        {
            var game = await resolver.ResolveGameAsync(sport, sourceRow.Key, canonical, ct);
            gameMap[canonical.SourceGameId] = game;
            rows++;

            if (canonical.Bout is { } bout)
                rows += await UpsertBoutAsync(game, bout, ct);
        }

        return rows;
    }

    /// <summary>The sport's games held under the provider's id, by that id: the first of any it names twice.</summary>
    private async Task<Dictionary<string, Game>> KnownByExternalIdAsync(Sport sport, Source sourceRow, CancellationToken ct)
        => (await db.Games
                .Where(g => g.SportId == sport.Id)
                .ToListAsync(ct))
            .Where(g => g.ExternalIds.ContainsKey(sourceRow.Key))
            .GroupBy(g => g.ExternalIds[sourceRow.Key])
            .ToDictionary(g => g.Key, g => g.First());

    /// <summary>The players who appear in a resolved stat line, upserted; the rest reported are not stored.</summary>
    private async Task<Dictionary<string, Player>> PersistParticipantsAsync(
        Sport sport, Source sourceRow, string sportKey, IReadOnlyList<CanonicalPlayer> players,
        List<(CanonicalPlayerStat Stat, Game Game)> resolved, CancellationToken ct)
    {
        var participated = resolved.Select(r => r.Stat.SourcePlayerId).ToHashSet();
        var participants = players.Where(p => participated.Contains(p.SourcePlayerId)).ToList();

        if (participants.Count < players.Count)
        {
            logger.LogInformation(
                "{Source}/{Sport}: {Kept} of {Total} reported players actually appeared; the rest are not stored",
                sourceRow.Key, sportKey, participants.Count, players.Count);
        }

        // The newest game each player appears in within this payload, which decides whether it
        // is allowed to say where the player is now (see UpsertPlayersAsync).
        var playedAt = resolved
            .GroupBy(r => r.Stat.SourcePlayerId)
            .ToDictionary(g => g.Key, g => g.Max(r => r.Game.StartsAt));

        return await UpsertPlayersAsync(sport, sourceRow.Key, participants, playedAt, ct);
    }

    /// <summary>Each resolved stat line onto its player and game, added or refreshed in the change tracker.</summary>
    /// <returns>The lines added or changed.</returns>
    private async Task<int> PersistStatLinesAsync(
        Sport sport, Source sourceRow, string sportKey, List<(CanonicalPlayerStat Stat, Game Game)> resolved,
        Dictionary<string, Player> playerMap, Func<CancellationToken, Task<long>> runId, bool live, CancellationToken ct)
    {
        var rows = 0;

        // Storage allows one line per player per game per source, and the key is the
        // *resolved* PlayerId — not the provider's athlete id. Those are not the same thing:
        // UpsertPlayersAsync falls back to matching on full name, so two source ids can land
        // on one Player row, and a source that reports a player under several stat categories
        // arrives with several lines to begin with. Either way two inserts with one key end
        // up in the same transaction and the whole day dies on a duplicate.
        //
        // So the batch is keyed the way the constraint is, after resolution, and a repeat
        // updates the row already in flight rather than adding a second.
        var written = new Dictionary<(int PlayerId, int GameId), PlayerGameStat>();
        var collisions = 0;

        // The live box's runs behind lines this write is about to meet. A line from the live box
        // is not a final's stats (IngestionJobs.StatsOwed), so a final write takes every such
        // line onto its own run even where the numbers have not moved — a final box that says
        // what the last live one said would otherwise leave the game owed on every sweep.
        var liveRuns = live || resolved.Count == 0
            ? []
            : await LiveRunsAsync(sourceRow, resolved.Select(r => r.Game.Id).Distinct().ToList(), ct);

        foreach (var (stat, game) in resolved)
        {
            if (!playerMap.TryGetValue(stat.SourcePlayerId, out var player))
                continue;

            // Second sighting of a key already handled in this batch. The row is in the change
            // tracker and not yet in the database, so the query below would not find it and we
            // would add a duplicate.
            if (written.TryGetValue((player.Id, game.Id), out var pending))
            {
                collisions++;

                if (pending.StatLine != stat.StatLineJson)
                {
                    pending.StatLine = stat.StatLineJson;
                    pending.CapturedAt = clock.GetUtcNow();
                    pending.IngestionRunId = await runId(ct);
                }

                continue;
            }

            var side = await SideAsync(sport, sourceRow.Key, stat, game, ct);

            // Upsert: a re-run of the same day refreshes the line rather than duplicating it.
            var existing = await db.PlayerGameStats.FirstOrDefaultAsync(
                s => s.PlayerId == player.Id && s.GameId == game.Id && s.SourceId == sourceRow.Id, ct);

            if (existing is null)
            {
                existing = new PlayerGameStat
                {
                    PlayerId = player.Id,
                    GameId = game.Id,
                    SourceId = sourceRow.Id,
                    TeamId = side,
                    StatLine = stat.StatLineJson,
                    CapturedAt = clock.GetUtcNow(),
                    IngestionRunId = await runId(ct)
                };

                db.PlayerGameStats.Add(existing);
                rows++;
            }
            else if (!SameStatLine(existing.StatLine, stat.StatLineJson)
                     || (side is not null && existing.TeamId != side)
                     || liveRuns.Contains(existing.IngestionRunId))
            {
                // A re-walk is also how a line stored before sides were recorded gets one, so
                // a line whose numbers have not moved is still refreshed when its side has.
                existing.StatLine = stat.StatLineJson;
                existing.TeamId = side ?? existing.TeamId;
                existing.CapturedAt = clock.GetUtcNow();
                existing.IngestionRunId = await runId(ct);
                rows++;
            }

            written[(player.Id, game.Id)] = existing;
        }

        if (collisions > 0)
        {
            // Worth saying out loud: it usually means two of the provider's athlete ids
            // resolved to one player, which is an entity-resolution question rather than a
            // storage one.
            logger.LogWarning(
                "{Source}/{Sport}: {Count} stat lines resolved onto a player/game already written; merged",
                sourceRow.Key, sportKey, collisions);
        }

        return rows;
    }

    /// <summary>
    /// The circumstances of each game the fetch described, to the game it names — one this
    /// payload carried, or one already held under the provider's id, as a stat line finds its
    /// game. A context for a game we hold neither way has nowhere to go and is dropped.
    /// </summary>
    /// <param name="knownByExternalId">The sport's games by the provider's id, where the stat lines already loaded them.</param>
    private async Task<int> PersistContextsAsync(
        Sport sport, Source sourceRow, IReadOnlyList<CanonicalGameContext> contexts, Dictionary<string, Game> gameMap,
        Dictionary<string, Game>? knownByExternalId, CancellationToken ct)
    {
        var rows = 0;

        if (contexts.Count > 0)
        {
            var writer = new GameContextWriter(db, clock);

            foreach (var context in contexts)
            {
                if (!gameMap.TryGetValue(context.SourceGameId, out var game))
                {
                    knownByExternalId ??= await KnownByExternalIdAsync(sport, sourceRow, ct);

                    if (!knownByExternalId.TryGetValue(context.SourceGameId, out game))
                        continue;
                }

                rows += await writer.UpsertAsync(game, sourceRow, context, ct);
            }
        }

        return rows;
    }

    /// <summary>
    /// A fight's card and result beside its game (ADR 0021): written on first sight, refreshed as
    /// the card changes and the result arrives. The provider is the authority on its own card, so
    /// every field follows it — except a result, which a later read that has lost it (a fight
    /// center gone quiet, the scoreboard standing in) does not erase.
    /// </summary>
    /// <returns>1 when the row was written or changed.</returns>
    private async Task<int> UpsertBoutAsync(Game game, CanonicalBout canonical, CancellationToken ct)
    {
        var bout = await db.Bouts.FirstOrDefaultAsync(b => b.GameId == game.Id, ct);

        if (bout is null)
        {
            bout = new Bout { GameId = game.Id };
            db.Bouts.Add(bout);
        }

        var before = (bout.EventId, bout.EventName, bout.EventShortName, bout.CardSegment, bout.MatchNumber, bout.WeightClass,
            bout.ScheduledRounds, bout.TitleFight, bout.HomeRecord, bout.AwayRecord, bout.Outcome, bout.Method,
            bout.MethodDetail, bout.EndRound, bout.EndSeconds);

        bout.EventId = canonical.EventId;
        bout.EventName = ColumnText.Fit(canonical.EventName, 160)!;
        bout.EventShortName = ColumnText.Fit(canonical.EventShortName, 64) ?? bout.EventShortName;
        bout.CardSegment = ColumnText.Fit(canonical.CardSegment, 32) ?? bout.CardSegment;
        bout.MatchNumber = canonical.MatchNumber ?? bout.MatchNumber;
        bout.WeightClass = ColumnText.Fit(canonical.WeightClass, 48) ?? bout.WeightClass;
        bout.ScheduledRounds = canonical.ScheduledRounds;
        bout.TitleFight = canonical.TitleFight;
        bout.HomeRecord = ColumnText.Fit(canonical.HomeRecord, 16) ?? bout.HomeRecord;
        bout.AwayRecord = ColumnText.Fit(canonical.AwayRecord, 16) ?? bout.AwayRecord;

        if (canonical.Outcome is not null || bout.Outcome is null)
        {
            bout.Outcome = canonical.Outcome;
            bout.Method = ColumnText.Fit(canonical.Method, 64) ?? (canonical.Outcome is null ? null : bout.Method);
            bout.MethodDetail = ColumnText.Fit(canonical.MethodDetail, 96);
            bout.EndRound = canonical.EndRound;
            bout.EndSeconds = canonical.EndSeconds;
        }

        // A no contest has no winner and so no score, whatever a scoreboard said before the ruling.
        if (bout.Outcome == BoutOutcome.NoContest && (game.HomeScore is not null || game.AwayScore is not null))
        {
            game.HomeScore = null;
            game.AwayScore = null;
        }

        var after = (bout.EventId, bout.EventName, bout.EventShortName, bout.CardSegment, bout.MatchNumber, bout.WeightClass,
            bout.ScheduledRounds, bout.TitleFight, bout.HomeRecord, bout.AwayRecord, bout.Outcome, bout.Method,
            bout.MethodDetail, bout.EndRound, bout.EndSeconds);

        if (db.Entry(bout).State != EntityState.Added && before == after)
            return 0;

        await db.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>
    /// The team a stat line was made for, when the source said and it is one of the game's two
    /// sides. A name that resolves to neither is not believed: storing it would credit a game
    /// to a club that was not in it, which is worse than falling back to the player's team.
    /// </summary>
    private async Task<int?> SideAsync(
        Sport sport, string sourceKey, CanonicalPlayerStat stat, Game game, CancellationToken ct)
    {
        if (stat.TeamName is null)
            return null;

        var team = await resolver.ResolveTeamAsync(
            sport, sourceKey, new CanonicalTeamRef(stat.TeamName, stat.SourceTeamId), ct);

        return team.Id == game.HomeTeamId || team.Id == game.AwayTeamId ? team.Id : null;
    }

    private async Task<Dictionary<string, Player>> UpsertPlayersAsync(
        Sport sport,
        string sourceKey,
        IReadOnlyList<CanonicalPlayer> canonicals,
        IReadOnlyDictionary<string, DateTimeOffset> playedAt,
        CancellationToken ct)
    {
        var map = new Dictionary<string, Player>();

        if (canonicals.Count == 0)
            return map;

        var existing = await db.Players.Where(p => p.SportId == sport.Id).ToListAsync(ct);

        // Each known player's most recent stored appearance. A payload older than that is
        // history, and history does not get to say where a player is now: a backfill walks
        // newest day first, so letting every day write the team left each player on the
        // oldest club the walk reached.
        var latest = await db.PlayerGameStats
            .Where(s => s.Game!.SportId == sport.Id)
            .GroupBy(s => s.PlayerId)
            .Select(g => new { g.Key, Latest = g.Max(s => s.Game!.StartsAt) })
            .ToDictionaryAsync(x => x.Key, x => x.Latest, ct);

        foreach (var canonical in canonicals)
        {
            // The team first, so a name shared by two people can be told apart by it — by the
            // provider's id where it gave one, since a fighter's team is the fighter, and two
            // fighters can share a name (ADR 0021).
            var team = canonical.TeamName is null
                ? null
                : await resolver.ResolveTeamAsync(sport, sourceKey,
                    new CanonicalTeamRef(canonical.TeamName, canonical.SourceTeamId), ct);

            var player = MatchPlayer(existing, canonical, sourceKey, team?.Id);

            if (player is null)
            {
                player = new Player
                {
                    SportId = sport.Id,
                    FullName = canonical.FullName,
                    Position = canonical.Position,
                    Status = canonical.Status
                };

                db.Players.Add(player);
                existing.Add(player);
            }

            if (!player.ExternalIds.ContainsKey(sourceKey))
            {
                player.ExternalIds = new Dictionary<string, string>(player.ExternalIds)
                {
                    [sourceKey] = canonical.SourcePlayerId
                };
            }

            // Players move between teams, so the team follows the newest game on record — and
            // only the newest. An older day re-walked keeps its side on the stat line instead.
            var current = !latest.TryGetValue(player.Id, out var last)
                          || !playedAt.TryGetValue(canonical.SourcePlayerId, out var played)
                          || played >= last;

            if (team is not null && current)
                player.TeamId = team.Id;

            map[canonical.SourcePlayerId] = player;
        }

        await db.SaveChangesAsync(ct);
        return map;
    }

    /// <summary>
    /// The stored player a source's athlete is, if any.
    ///
    /// <para>
    /// By the source's own id first — that is identity. By name only as a fallback, for a player
    /// another source created, and never onto a player this source already knows under a
    /// different id: that is a different person with the same name. It used to take any name
    /// match, so two ESPN athletes called Will Smith became one player and one of every pair of
    /// their stat lines was thrown away as a duplicate (ADR 0009's open item). Where more than one
    /// name match remains, the one on the athlete's team is the one; where the team cannot decide
    /// between them, none is taken and a new player is made — a duplicate row can be merged
    /// later, a merged person cannot be split.
    /// </para>
    /// </summary>
    public static Player? MatchPlayer(
        IReadOnlyList<Player> existing, CanonicalPlayer canonical, string sourceKey, int? teamId)
    {
        var byId = existing.FirstOrDefault(p =>
            p.ExternalIds.TryGetValue(sourceKey, out var id) && id == canonical.SourcePlayerId);

        if (byId is not null)
            return byId;

        var byName = existing
            .Where(p => string.Equals(p.FullName, canonical.FullName, StringComparison.Ordinal)
                        && !p.ExternalIds.ContainsKey(sourceKey))
            .ToList();

        if (byName.Count <= 1)
            return byName.FirstOrDefault(p => teamId is null || p.TeamId is null || p.TeamId == teamId);

        var onTeam = byName.Where(p => teamId is not null && p.TeamId == teamId).ToList();
        return onTeam.Count == 1 ? onTeam[0] : null;
    }
}
