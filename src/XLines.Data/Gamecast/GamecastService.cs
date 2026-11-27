using System.Text.Json;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data.CrossReference;
using Microsoft.EntityFrameworkCore;

namespace XLines.Data.Gamecast;

/// <summary>
/// What the Gamecast window reads for one game: the header (score, clock, situation and the
/// pre-game close), its plays, its drives, the probability line and the scoring summary
/// (spec 2026-10-24 §6.1, §6.4).
///
/// <para>
/// The window appends rather than reloads. It opens on <see cref="SnapshotAsync(int, CancellationToken)"/>
/// (shared across circuits by the web host), and every open Gamecast on every circuit hears the
/// same <c>plays:{id}</c> notice and asks <see cref="ChangesSinceAsync"/> for the plays and drives
/// stamped since the newest it has read: one query per table down <c>ix_game_play_game_updated</c>
/// and <c>ix_game_drive_game_updated</c>, untracked, projected straight to records, with no
/// GROUP BY — the drives' spans are derived from the plays held (<see cref="GamecastDrives"/>).
/// The header is asked when the window opens and on a <c>games:{id}</c> notice.
/// </para>
///
/// <para>
/// Nothing here hands the window an entity or a jsonb string. The linescore, the play
/// situations and baseball's diamond are parsed once, here, and a value that cannot be read
/// arrives as nothing rather than as an exception in a render: every ESPN field is optional
/// (spec §7), and a window that throws over a malformed situation shows no game at all.
/// </para>
///
/// <para>
/// A context per call, from the factory, as the Blazor panels take theirs: a window may ask for
/// its header and its plays at once, and one scoped context would refuse the second.
/// </para>
/// </summary>
public class GamecastService(IDbContextFactory<XLinesDbContext> contexts, XLines.Core.Contracts.IDataTier? tier = null)
{
    /// <summary>
    /// Who is reading, by tier (ADR 0024): the close on the header is the board's, read for the
    /// same person, so a visitor to a shared desk is not handed here the ESPN close the row withheld.
    /// The header's state of play is the board's game too, blanked the same way
    /// (<see cref="PersonalUseColumns"/>), and the plays and drives keep to the visible sources.
    /// </summary>
    private XLines.Core.Contracts.IDataTier Tier { get; } = tier ?? XLines.Core.Contracts.FullDataTier.Instance;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The game with its teams, the state of play and the pre-game close; null when there is no
    /// such game.
    ///
    /// <para>
    /// The close is the board's own number (<see cref="BoardService.GetRowAsync"/>): the best home
    /// handicap and the best total, read market-first exactly as the board reads it, so the
    /// reference line on the chart is the number the operator saw on the row they opened it from.
    /// </para>
    /// </summary>
    public async Task<GamecastHeader?> HeaderAsync(int gameId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        var row = await new BoardService(db, Tier).GetRowAsync(gameId, ct);

        if (row is null)
            return null;

        var game = row.Game;
        var (home, away) = Linescores.Parse(game.Linescore);

        var spread = row.Spread.First?.Line;
        var total = row.Total.First?.Line ?? row.Total.Second?.Line;

        return new GamecastHeader(
            GameId: game.Id,
            SportKey: game.Sport?.Key ?? string.Empty,
            SportName: game.Sport?.Name ?? string.Empty,
            Home: Team(game.HomeTeamId, game.HomeTeam),
            Away: Team(game.AwayTeamId, game.AwayTeam),
            StartsAt: game.StartsAt,
            Status: game.Status,
            HomeScore: game.HomeScore,
            AwayScore: game.AwayScore,
            Period: game.Period,
            DisplayClock: game.DisplayClock,
            ClockSeconds: game.ClockSeconds,
            StatusDetail: game.StatusDetail,
            PossessionTeamId: game.PossessionTeamId,
            Down: game.Down,
            Distance: game.Distance,
            YardLine: game.YardLine,
            DownDistanceText: game.DownDistanceText,
            PossessionText: game.PossessionText,
            IsRedZone: game.IsRedZone,
            HomeTimeouts: game.HomeTimeouts,
            AwayTimeouts: game.AwayTimeouts,
            LastPlay: game.LastPlay,
            HomeLinescore: home,
            AwayLinescore: away,
            Diamond: Read<GamecastDiamond>(game.Situation),
            LiveUpdatedAt: game.LiveUpdatedAt,
            Close: spread is null && total is null ? null : new GamecastClose(spread, total, row.PricesAreClosing));
    }

    /// <summary>
    /// Every play of the game, in the provider's order, ties in the order they were stored. Its
    /// own read rather than "after zero": the adapter stores a play ESPN sent no sequence for at
    /// zero, and "after zero" would leave it out.
    /// </summary>
    public async Task<IReadOnlyList<GamecastPlay>> PlaysAsync(int gameId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        return await PlaysAsync((await VisiblePlaysAsync(db, ct)).Where(p => p.GameId == gameId), ct);
    }

    /// <summary>
    /// The plays strictly after <paramref name="afterSequence"/>, in the provider's order, ties
    /// in the order they were stored. The hot path: see the class summary. Every play is
    /// <see cref="PlaysAsync(int, CancellationToken)"/>.
    ///
    /// <para>
    /// A play ESPN rewrites after a review may keep its sequence or take a new one
    /// (GamecastWriter updates it in place), so a window that wants corrections reads the drive
    /// it has open again and matches plays by id.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GamecastPlay>> PlaysSinceAsync(
        int gameId, long afterSequence, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        return await PlaysAsync((await VisiblePlaysAsync(db, ct)).Where(p => p.GameId == gameId && p.Sequence > afterSequence), ct);
    }

    /// <summary>The scoring plays, in order: the compact "how it got to 17–20" view.</summary>
    public async Task<IReadOnlyList<GamecastPlay>> ScoringPlaysAsync(int gameId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        return await PlaysAsync((await VisiblePlaysAsync(db, ct)).Where(p => p.GameId == gameId && p.IsScoring), ct);
    }

    /// <summary>
    /// The drives in order, each with how many of its plays we hold and the sequence range they
    /// span — enough for a collapsed drive's result line ("7 plays, 62 yds, 3:48 · TD") and for
    /// opening it onto its plays without a query per drive.
    ///
    /// <para>
    /// <see cref="GamecastDrive.OffensivePlays"/> is the provider's count, which leaves out
    /// penalties and kicks; <see cref="GamecastDrive.PlayCount"/> is the rows we hold. The result
    /// line wants the first, the drive's play list the second.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GamecastDrive>> DrivesAsync(int gameId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        var visible = await db.VisibleSourceIdsAsync(Tier, ct);

        var (drives, _) = await ReadDrivesAsync(db.GameDrives.FromVisible(visible).Where(d => d.GameId == gameId), ct);
        if (drives.Count == 0)
            return [];

        // The spans from the plays' sequence and drive alone, derived as the window derives them
        // (GamecastDrives.WithSpans) rather than grouped in the database.
        var spans = await db.GamePlays.FromVisible(visible)
            .AsNoTracking()
            .Where(p => p.GameId == gameId && p.DriveSeq != null)
            .Select(p => new { p.Sequence, p.DriveSeq })
            .ToListAsync(ct);

        return GamecastDrives.WithSpans(drives, spans.Select(p => (p.Sequence, p.DriveSeq)));
    }

    // ---- What the window reads: once whole, then what changed --------------------------------

    /// <summary>
    /// How far behind its watermark <see cref="ChangesSinceAsync"/> reads again.
    ///
    /// <para>
    /// <see cref="GamePlay.UpdatedAt"/> is the writer's clock, taken before its save commits. Two
    /// writers — the feed and the backport, or the feed on two hosts whose clocks disagree by a
    /// second — can commit a row whose stamp is at or a little before the newest a window has
    /// already read. Reading from the watermark less this much catches that row; the window
    /// matches by id, so the few rows read twice are drawn once. The cost is those few rows per
    /// notice, down <c>ix_game_play_game_updated</c>.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ChangeOverlap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The sources this reader may see (ADR 0024): null for every one. What a snapshot shared
    /// across readers is filed under (<see cref="TierKey"/>), so one tier is never served another's.
    /// </summary>
    public async Task<int[]?> VisibleSourcesAsync(CancellationToken ct = default)
    {
        if (Tier.CanSeePersonalUseSources)
            return null;

        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.VisibleSourceIdsAsync(Tier, ct);
    }

    /// <summary>
    /// A visible-sources answer as a cache key: <c>all</c>, or the ids in order (<c>v:</c> alone when
    /// none is visible). Two readers with the same key see exactly the same rows.
    /// </summary>
    public static string TierKey(int[]? visible)
        => visible is null ? "all" : "v:" + string.Join(',', visible.Order());

    /// <summary>
    /// Every play and drive of the game this reader may see, the drives with their spans, and the
    /// newest stamp among them as the watermark the window reads changes after.
    /// </summary>
    public async Task<GamecastRows> SnapshotAsync(int gameId, CancellationToken ct = default)
        => await SnapshotAsync(gameId, await VisibleSourcesAsync(ct), ct);

    /// <summary>
    /// <see cref="SnapshotAsync(int, CancellationToken)"/> for an answer of
    /// <see cref="VisibleSourcesAsync"/> already in hand: what a shared snapshot is loaded with, so
    /// the rows it holds are exactly the ones its key names.
    /// </summary>
    public async Task<GamecastRows> SnapshotAsync(int gameId, int[]? visible, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        var (plays, newestPlay) = await ReadPlaysAsync(db.GamePlays.FromVisible(visible).Where(p => p.GameId == gameId), ct);
        var (drives, newestDrive) = await ReadDrivesAsync(db.GameDrives.FromVisible(visible).Where(d => d.GameId == gameId), ct);

        return new GamecastRows(plays, GamecastDrives.WithSpans(drives, plays), Later(newestPlay, newestDrive));
    }

    /// <summary>
    /// The plays and drives stamped at or after <paramref name="since"/> less
    /// <see cref="ChangeOverlap"/> — new ones, and old ones the provider rewrote, in any drive —
    /// with the newest stamp read (never earlier than <paramref name="since"/>) as the next
    /// watermark. Every row when <paramref name="since"/> is null. The drives come without spans:
    /// the window derives them from the plays it holds (<see cref="GamecastDrives.WithSpans"/>).
    ///
    /// <para>
    /// One read per table on a <c>plays:{id}</c> notice, in place of the plays after the last
    /// sequence held, the drives with a GROUP BY over every play, and the drive in progress read
    /// again. Nothing here learns of a deletion, because there are none: the writer never deletes
    /// a play or a drive (<see cref="GamePlay"/>), and a game deleted takes its plays with it and
    /// leaves the window with no header, which it reloads on.
    /// </para>
    /// </summary>
    public async Task<GamecastRows> ChangesSinceAsync(int gameId, DateTimeOffset? since, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        var visible = await db.VisibleSourceIdsAsync(Tier, ct);
        var plays = db.GamePlays.FromVisible(visible).Where(p => p.GameId == gameId);
        var drives = db.GameDrives.FromVisible(visible).Where(d => d.GameId == gameId);

        if (since is { } watermark)
        {
            var from = watermark - ChangeOverlap;
            plays = plays.Where(p => p.UpdatedAt >= from);
            drives = drives.Where(d => d.UpdatedAt >= from);
        }

        var (changedPlays, newestPlay) = await ReadPlaysAsync(plays, ct);
        var (changedDrives, newestDrive) = await ReadDrivesAsync(drives, ct);

        return new GamecastRows(changedPlays, changedDrives, Later(since, Later(newestPlay, newestDrive)));
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>The drives in order, without spans, and the newest stamp among them.</summary>
    private static async Task<(List<GamecastDrive> Drives, DateTimeOffset? Newest)> ReadDrivesAsync(
        IQueryable<GameDrive> drives, CancellationToken ct)
    {
        var rows = await drives
            .AsNoTracking()
            .OrderBy(d => d.Sequence)
            .ThenBy(d => d.Id)
            .Select(d => new
            {
                d.Id,
                d.Sequence,
                d.TeamId,
                TeamAbbrev = d.Team!.Abbrev,
                d.Description,
                d.Result,
                d.Yards,
                d.OffensivePlays,
                d.TimeElapsed,
                d.IsScore,
                d.IsCurrent,
                d.StartPeriod,
                d.StartClock,
                d.StartYardLine,
                d.EndPeriod,
                d.EndClock,
                d.EndYardLine,
                d.UpdatedAt
            })
            .ToListAsync(ct);

        var list = rows.Select(d => new GamecastDrive(
                d.Sequence, d.TeamId, d.TeamAbbrev, d.Description, d.Result, d.Yards, d.OffensivePlays,
                d.TimeElapsed, d.IsScore, d.IsCurrent, d.StartPeriod, d.StartClock, d.StartYardLine,
                d.EndPeriod, d.EndClock, d.EndYardLine,
                PlayCount: 0, FirstSequence: null, LastSequence: null, Id: d.Id))
            .ToList();

        return (list, rows.Count == 0 ? null : rows.Max(d => d.UpdatedAt));
    }

    /// <summary>
    /// The probability line over play sequence, as fractions of one: home win, home covering the
    /// spread, and the total going over. Plays the provider gave no probability for are left out
    /// rather than drawn as zero.
    /// </summary>
    public async Task<IReadOnlyList<GamecastProbability>> ProbabilitySeriesAsync(
        int gameId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        var rows = await (await VisiblePlaysAsync(db, ct))
            .AsNoTracking()
            .Where(p => p.GameId == gameId
                && (p.HomeWinPct != null || p.SpreadCoverPctHome != null || p.TotalOverPct != null))
            .OrderBy(p => p.Sequence)
            .Select(p => new { p.Sequence, p.Period, p.Clock, p.HomeWinPct, p.SpreadCoverPctHome, p.TotalOverPct })
            .ToListAsync(ct);

        return rows
            .Select(p => new GamecastProbability(
                p.Sequence, p.Period, p.Clock,
                (double?)p.HomeWinPct, (double?)p.SpreadCoverPctHome, (double?)p.TotalOverPct))
            .ToList();
    }

    /// <summary>
    /// Why a finished game holds no plays, when the backport knows (<see cref="GamecastBackportMiss"/>):
    /// how often it asked, when it asks again and what the last ask came to. Null when it has not
    /// missed the game, or when the reader may not see the source it asked (ADR 0024). Asked only
    /// for a final with no plays held, so it is one primary-key read, once per window.
    /// </summary>
    public async Task<GamecastMiss?> BackportMissAsync(int gameId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        var visible = await db.VisibleSourceIdsAsync(Tier, ct);

        return await db.GamecastBackportMisses
            .AsNoTracking()
            .Where(m => m.GameId == gameId && (visible == null || visible.Contains(m.SourceId)))
            .Select(m => new GamecastMiss(m.Attempts, m.LastTriedAt, m.RetryAt, m.Reason))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The plays this reader may see (ADR 0024): every play on a local desk or for the operator,
    /// whose answer is the table as it was; for a visitor only a displayable source's, which today
    /// is none, so the window shows the empty state it shows before a game's first play.
    /// </summary>
    private async Task<IQueryable<GamePlay>> VisiblePlaysAsync(XLinesDbContext db, CancellationToken ct)
        => db.GamePlays.FromVisible(await db.VisibleSourceIdsAsync(Tier, ct));

    private static async Task<IReadOnlyList<GamecastPlay>> PlaysAsync(IQueryable<GamePlay> plays, CancellationToken ct)
        => (await ReadPlaysAsync(plays, ct)).Plays;

    /// <summary>The plays in order, as records, and the newest stamp among them.</summary>
    private static async Task<(List<GamecastPlay> Plays, DateTimeOffset? Newest)> ReadPlaysAsync(
        IQueryable<GamePlay> plays, CancellationToken ct)
    {
        var rows = await plays
            .AsNoTracking()
            .OrderBy(p => p.Sequence)
            .ThenBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.Sequence,
                p.DriveSeq,
                p.Period,
                p.Clock,
                p.ClockSeconds,
                p.Type,
                p.Text,
                p.HomeScore,
                p.AwayScore,
                p.IsScoring,
                p.IsTurnover,
                p.IsPenalty,
                p.Yardage,
                p.TeamId,
                p.StartSituation,
                p.EndSituation,
                p.HomeWinPct,
                p.SpreadCoverPctHome,
                p.TotalOverPct,
                p.Wallclock,
                p.Modified,
                p.UpdatedAt
            })
            .ToListAsync(ct);

        var list = rows
            .Select(p => new GamecastPlay(
                p.Id, p.Sequence, p.DriveSeq, p.Period, p.Clock, p.ClockSeconds, p.Type, p.Text,
                p.HomeScore, p.AwayScore, p.IsScoring, p.IsTurnover, p.IsPenalty, p.Yardage, p.TeamId,
                Read<GamecastSituation>(p.StartSituation), Read<GamecastSituation>(p.EndSituation),
                (double?)p.HomeWinPct, (double?)p.SpreadCoverPctHome, (double?)p.TotalOverPct,
                p.Wallclock, p.Modified))
            .ToList();

        return (list, rows.Count == 0 ? null : rows.Max(p => p.UpdatedAt));
    }

    private static GamecastTeam Team(int id, Team? team)
        => new(id, team?.Name ?? string.Empty, team?.Abbrev ?? string.Empty);

    /// <summary>A small jsonb object as its record, or null when there is none or it cannot be read.</summary>
    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One side of the game, as the window labels it.</summary>
public sealed record GamecastTeam(int Id, string Name, string Abbrev);

/// <summary>
/// The pre-game market: the home side's handicap and the total. <see cref="IsClosing"/> is false
/// only for a game not yet started, whose number is still the live scan.
/// </summary>
public sealed record GamecastClose(decimal? HomeSpread, decimal? Total, bool IsClosing);

/// <summary>Baseball's situation (<see cref="Game.Situation"/>): the count, the runners and the matchup.</summary>
public sealed record GamecastDiamond(
    int? Balls, int? Strikes, int? Outs, bool OnFirst, bool OnSecond, bool OnThird, string? Batter, string? Pitcher);

/// <summary>
/// The game and its state of play, for the title and the field strip. <see cref="YardLine"/> is
/// yards from the <b>home</b> goal line (see <see cref="Game.YardLine"/>). The linescores are
/// empty before a period has been played.
/// </summary>
public sealed record GamecastHeader(
    int GameId,
    string SportKey,
    string SportName,
    GamecastTeam Home,
    GamecastTeam Away,
    DateTimeOffset StartsAt,
    GameStatus Status,
    int? HomeScore,
    int? AwayScore,
    int? Period,
    string? DisplayClock,
    int? ClockSeconds,
    string? StatusDetail,
    int? PossessionTeamId,
    int? Down,
    int? Distance,
    int? YardLine,
    string? DownDistanceText,
    string? PossessionText,
    bool? IsRedZone,
    int? HomeTimeouts,
    int? AwayTimeouts,
    string? LastPlay,
    IReadOnlyList<int> HomeLinescore,
    IReadOnlyList<int> AwayLinescore,
    GamecastDiamond? Diamond,
    DateTimeOffset? LiveUpdatedAt,
    GamecastClose? Close);

/// <summary>
/// Down, distance and field position either side of a snap. <see cref="YardLine"/> is on the
/// home-goal frame; <see cref="TeamId"/> is our <see cref="Team.Id"/> for the side with the ball.
/// </summary>
public sealed record GamecastSituation(
    int? Down,
    int? Distance,
    int? YardLine,
    int? YardsToEndzone,
    string? DownDistanceText,
    string? PossessionText,
    int? TeamId);

/// <summary>One play, as a drive-log row draws it. Probabilities are fractions of one.</summary>
public sealed record GamecastPlay(
    long Id,
    long Sequence,
    int? DriveSeq,
    int? Period,
    string? Clock,
    int? ClockSeconds,
    string? Type,
    string? Text,
    int? HomeScore,
    int? AwayScore,
    bool IsScoring,
    bool IsTurnover,
    bool IsPenalty,
    int? Yardage,
    int? TeamId,
    GamecastSituation? Start,
    GamecastSituation? End,
    double? HomeWinPct,
    double? SpreadCoverPctHome,
    double? TotalOverPct,
    DateTimeOffset? Wallclock,
    DateTimeOffset? Modified);

/// <summary>
/// One drive. <see cref="PlayCount"/> and the sequence range are the plays we hold under it
/// (<see cref="GamePlay.DriveSeq"/> = <see cref="Sequence"/>); both are empty for a drive with none yet.
/// <see cref="Id"/> is the stored row's, what a correction is matched by when the provider
/// renumbers the drive; zero for a drive built by hand.
/// </summary>
public sealed record GamecastDrive(
    int Sequence,
    int? TeamId,
    string? TeamAbbrev,
    string? Description,
    string? Result,
    int? Yards,
    int? OffensivePlays,
    string? TimeElapsed,
    bool IsScore,
    bool IsCurrent,
    int? StartPeriod,
    string? StartClock,
    int? StartYardLine,
    int? EndPeriod,
    string? EndClock,
    int? EndYardLine,
    int PlayCount,
    long? FirstSequence,
    long? LastSequence,
    long Id = 0);

/// <summary>
/// What the window reads in one go: plays in order (ties by id), drives in order, and the newest
/// <see cref="GamePlay.UpdatedAt"/>/<see cref="GameDrive.UpdatedAt"/> among the rows read — the
/// database's own clock, never the reader's — as the watermark for the next read. Null when no row
/// has been read. Immutable: one snapshot may be handed to every window open on the game.
/// </summary>
public sealed record GamecastRows(
    IReadOnlyList<GamecastPlay> Plays,
    IReadOnlyList<GamecastDrive> Drives,
    DateTimeOffset? Watermark)
{
    public static readonly GamecastRows Empty = new([], [], null);
}

/// <summary>Drive arithmetic done in memory, over the plays a reader already holds.</summary>
public static class GamecastDrives
{
    /// <summary>
    /// The drives with <see cref="GamecastDrive.PlayCount"/> and the sequence range set from the
    /// plays filed under each (<see cref="GamecastPlay.DriveSeq"/> = <see cref="GamecastDrive.Sequence"/>):
    /// what a GROUP BY over the game's plays returned, without asking the database on every notice.
    /// A drive with no play held has none; a play under no drive, or under one not listed, counts
    /// for nothing. The order and every other field are the drives' own.
    /// </summary>
    public static IReadOnlyList<GamecastDrive> WithSpans(IReadOnlyList<GamecastDrive> drives, IEnumerable<GamecastPlay> plays)
        => WithSpans(drives, plays.Select(p => (p.Sequence, p.DriveSeq)));

    /// <inheritdoc cref="WithSpans(IReadOnlyList{GamecastDrive}, IEnumerable{GamecastPlay})"/>
    public static IReadOnlyList<GamecastDrive> WithSpans(IReadOnlyList<GamecastDrive> drives, IEnumerable<(long Sequence, int? DriveSeq)> plays)
    {
        if (drives.Count == 0)
            return [];

        var spans = new Dictionary<int, (int Count, long First, long Last)>();

        foreach (var (sequence, driveSeq) in plays)
        {
            if (driveSeq is not { } drive)
                continue;

            spans[drive] = spans.TryGetValue(drive, out var span)
                ? (span.Count + 1, Math.Min(span.First, sequence), Math.Max(span.Last, sequence))
                : (1, sequence, sequence);
        }

        var result = new GamecastDrive[drives.Count];
        for (var i = 0; i < drives.Count; i++)
        {
            var d = drives[i];
            result[i] = spans.TryGetValue(d.Sequence, out var span)
                ? d with { PlayCount = span.Count, FirstSequence = span.First, LastSequence = span.Last }
                : d with { PlayCount = 0, FirstSequence = null, LastSequence = null };
        }

        return result;
    }
}

/// <summary>
/// A finished game the backport asked ESPN for and came away without plays: how many times, when
/// last, when it is due again, and why the last ask came away empty ("no plays", or the error).
/// </summary>
public sealed record GamecastMiss(int Attempts, DateTimeOffset LastTriedAt, DateTimeOffset RetryAt, string? Reason);

/// <summary>One point on the probability line, at a play. Fractions of one; null where the provider gave none.</summary>
public sealed record GamecastProbability(
    long Sequence,
    int? Period,
    string? Clock,
    double? HomeWin,
    double? SpreadCoverHome,
    double? TotalOver);
