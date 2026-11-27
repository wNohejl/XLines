using System.Security.Cryptography;
using System.Text.Json;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Data;
using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services;

/// <summary>
/// Follows games in play, play by play (spec 2026-10-24 §6.3).
///
/// <para>
/// Every <see cref="GamecastOptions.Poll"/> it asks which games are on — the scheduler's own
/// question, <see cref="IngestionJobs.InPlay"/>, so the feed never follows a game the live pass
/// would not refresh — and for each reads ESPN's summary and writes it through
/// <see cref="GamecastWriter"/>. A write that changed something raises <c>plays:{gameId}</c>, and
/// a Gamecast window watching that appends the plays; a poll that saw nothing new writes nothing
/// and raises nothing. The core API's probabilities are a second request, asked every
/// <see cref="GamecastOptions.ProbabilityPoll"/> once the summary calls the game live, and laid
/// onto every write in between, so a play keeps its spread-cover and total-over numbers between
/// asks rather than flickering to empty.
/// </para>
///
/// <para>
/// The box score is live too (§6.1), and costs no request: every
/// <see cref="GamecastOptions.BoxPoll"/> the summary being read anyway is also parsed for its box,
/// by the results pass's own parse, and the players' lines are written through the results
/// pass's path under <see cref="IngestionJobs.EspnGamecastLive"/>, a job apart from the final
/// box's, because a mid-game line is not a final's stats. Only the lines and the players they need — never the
/// game, whose status and score are the scoreboard pass's — and a box that moved no line writes
/// no run and raises no notice.
/// </para>
///
/// <para>
/// One port for the whole poll, and one pacer for the process: every ESPN request, the feed's and
/// everyone else's, waits its turn at <see cref="EspnPacer"/>, <c>RequestDelay</c> apart. The
/// feed's reads are marked (<see cref="GamecastRequest"/>): each is asked once and given
/// <see cref="GamecastOptions.ReadTimeout"/> — the next poll is its retry — and a watched game's
/// goes ahead of any other request waiting, so a backfill or a scoreboard pass running beside the
/// feed costs the game someone has open one request's gap at most.
/// </para>
///
/// <para>
/// The game a window is watching comes before everything. It is read first in every poll, every
/// poll, whatever it says — at a break, past the ceiling, failing — and never backed off: every
/// poll is its retry. A game nobody watches adapts: a live one is read every
/// <see cref="GamecastOptions.UnwatchedPoll"/>, and every <see cref="GamecastOptions.BreakPoll"/>
/// while its header says halftime or the end of a quarter. The polls fall on a fixed beat, a
/// <see cref="GamecastOptions.Poll"/> apart however long each takes. And a read whose gamecast
/// hashes the same as the last one written is not written again: the writer would load the game
/// only to find nothing to save. The hash is held in memory, so a restart, or a failed write,
/// writes in full once.
/// </para>
///
/// <para>
/// When a game goes final the feed writes the final summary, clears the game's current drive —
/// a drive ESPN stopped calling current can otherwise stay current here for good — and writes the
/// box score once, through the path the results sweep takes, so the final box is on the desk
/// without waiting for the sweep. Then it lets the game go. "Goes final" is read from ESPN's
/// summary, not from our row: the scoreboard pass often lands the final first, and the summary
/// is cached apart and can still say live for a poll or two. So a game that drops out of play
/// while followed is read on until its summary says final too, up to <see cref="LeavingReads"/>
/// reads, and finished regardless after that.
/// </para>
///
/// <para>
/// Two lanes, so no game in play is left without plays. The fast lane is the first
/// <see cref="GamecastOptions.MaxFollowedGames"/> games, read every poll (an unwatched one as its
/// cadence falls due): the games a window is watching (<see cref="GamecastLanes.Watched"/>) first,
/// all of them however many, then the healthy before the ones in trouble, earliest start first — so
/// the game someone has open takes a slot from one nobody is looking at. Every other game on is in the slow lane, read in turn by game id,
/// <see cref="GamecastOptions.SlowLanePerPoll"/> a poll: on a seven-game window each of the three
/// past the ceiling is read every third poll instead of not at all. A slow game keeps what the feed
/// holds for it between its turns — its probabilities, its box cadence — and the probability and
/// box cadences are clocks, not counts, so a turn asks for probabilities only when one is due. A
/// slow game that leaves play is read every poll until it is finished, as any leaving game is.
/// After every poll the lanes are reported to <see cref="GamecastLanes"/>, where a window reads
/// which lane its game is in and when it was last read.
/// </para>
///
/// <para>
/// Bounded three times over. At most <see cref="GamecastOptions.MaxFollowedGames"/> plus
/// <see cref="GamecastOptions.SlowLanePerPoll"/> summaries a poll, and said once when the ceiling
/// bites rather than every fifteen seconds. A failure on one game is logged and the rest of the poll carries on, as the loop
/// carries on past a failed poll. And a game that fails, or whose summary stays "scheduled", for
/// <see cref="FailuresBeforeBackOff"/> reads in a row is backed off for a growing while and gives
/// its slot to the healthy games — a stale event id would otherwise hold slot one for the day
/// the game stays in play. A crashed feed would leave every window silent, which is worse than a
/// game missing a poll.
/// </para>
///
/// <para>
/// Each game is written in a fresh scope, so one game's change tracker never carries into the
/// next game's save. What the feed holds between polls — the games followed, when each last asked
/// for probabilities, the games it has finished with, the games in trouble, the slow lane's turn — is in memory: a
/// restart re-reads what is on and loses nothing the store does not already hold.
/// </para>
/// </summary>
public class GamecastFeed(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    GamecastLanes lanes,
    ILogger<GamecastFeed> logger,
    TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The run row the final box score is recorded under, beside the results sweep's.</summary>
    public const string JobKey = "espn:gamecast";

    /// <summary>
    /// Reads a game gets after it leaves play here while its summary has not yet said final:
    /// eight polls, about two minutes, comfortably past ESPN's summary cache.
    /// </summary>
    public const int LeavingReads = 8;

    /// <summary>Failed or "scheduled" reads in a row before a game is backed off.</summary>
    public const int FailuresBeforeBackOff = 4;

    /// <summary>The longest a game in trouble waits between asks.</summary>
    private static readonly TimeSpan MaxBackOff = TimeSpan.FromMinutes(15);

    private readonly GamecastOptions _options = options.Value.Gamecast;

    /// <summary>A game being followed, and the probabilities last fetched for it.</summary>
    private sealed class Followed(string sport, string eventId, DateTimeOffset startsAt)
    {
        public string Sport { get; } = sport;
        public string EventId { get; } = eventId;
        public DateTimeOffset StartsAt { get; } = startsAt;
        public DateTimeOffset? ProbabilitiesAt { get; set; }
        public IReadOnlyDictionary<string, CanonicalPlayProbability> Probabilities { get; set; }
            = new Dictionary<string, CanonicalPlayProbability>();

        /// <summary>When the live box score was last written from the summary, if it has been.</summary>
        public DateTimeOffset? BoxAt { get; set; }

        /// <summary>Reads since the game left play here, its summary not yet final.</summary>
        public int ReadsSinceLeaving { get; set; }

        /// <summary>
        /// When an unwatched game is next due a read, from what its last read said; null reads at
        /// once. A watched game is read every poll and never looks at it.
        /// </summary>
        public DateTimeOffset? NextReadAt { get; set; }

        /// <summary>
        /// The last gamecast written for the game, hashed; null until a write lands, and again
        /// after one fails. A read that hashes the same has nothing for the writer to find.
        /// </summary>
        public string? Fingerprint { get; set; }
    }

    /// <summary>A game whose reads keep failing or keep saying "scheduled", and when to ask it next.</summary>
    private sealed class Trouble
    {
        public int Failures { get; set; }
        public DateTimeOffset RetryAt { get; set; }
    }

    private readonly Dictionary<int, Followed> _followed = [];

    /// <summary>
    /// Games seen final and let go, kept until they leave play here too: ESPN's summary can call a
    /// game final a poll or two before the scoreboard pass marks our row, and without this the feed
    /// would take it up again and write its box score a second time.
    /// </summary>
    private readonly HashSet<int> _finished = [];

    /// <summary>Games in trouble, forgotten when they recover or leave play.</summary>
    private readonly Dictionary<int, Trouble> _trouble = [];

    /// <summary>When each game's summary was last read, kept while the game is on or followed.</summary>
    private readonly Dictionary<int, DateTimeOffset> _lastReadAt = [];

    /// <summary>The slow lane's turn: the id of the slow game read last, so the next turn starts after it.</summary>
    private int _slowCursor;

    private bool _ceilingReported;
    private bool _noSourceReported;

    /// <summary>The games the feed holds state for after the last poll, by id: both lanes, and games leaving play.</summary>
    public IReadOnlyCollection<int> Following => _followed.Keys;

    private int SlowLanePerPoll => Math.Max(0, _options.SlowLanePerPoll);

    /// <summary>
    /// The configured cadences, or their defaults when configuration gives zero or less — as
    /// <see cref="IngestionScheduler"/> reads its own: <c>Task.Delay</c> throws on a negative
    /// delay, which would take the host down from the loop's catch, and zero would spin.
    /// </summary>
    private TimeSpan Poll => Positive(_options.Poll, new GamecastOptions().Poll);

    private TimeSpan ProbabilityPoll => Positive(_options.ProbabilityPoll, new GamecastOptions().ProbabilityPoll);

    private TimeSpan BoxPoll => Positive(_options.BoxPoll, new GamecastOptions().BoxPoll);

    private TimeSpan ReadTimeout => Positive(_options.ReadTimeout, new GamecastOptions().ReadTimeout);

    /// <summary>An unwatched live game's gap, never shorter than the poll that reads it.</summary>
    private TimeSpan UnwatchedPoll => Max(Poll, Positive(_options.UnwatchedPoll, new GamecastOptions().UnwatchedPoll));

    /// <summary>An unwatched game's gap at a break, never shorter than the poll that reads it.</summary>
    private TimeSpan BreakPoll => Max(Poll, Positive(_options.BreakPoll, new GamecastOptions().BreakPoll));

    private static TimeSpan Positive(TimeSpan configured, TimeSpan fallback)
        => configured > TimeSpan.Zero ? configured : fallback;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// Polls on a fixed beat: a <see cref="PeriodicTimer"/>'s ticks are a <see cref="Poll"/>
    /// apart from when the feed started, however long a poll took, where a delay after each poll
    /// added the poll's own length to every gap and a watched game's plays drifted later through
    /// the game. A poll that overruns its tick starts the next at once; ticks it missed are not
    /// made up. A failed poll waits for the next tick too, rather than spinning on the database.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Gamecast feed is switched off (Ingestion:Gamecast:Enabled)");
            return;
        }

        logger.LogInformation(
            "Gamecast feed started: {Sports}, every {Poll} for a watched game, {Unwatched} for one nobody watches ({Break} at a break), up to {Max} games every poll and {Slow} more a poll in turn",
            string.Join(", ", _options.EffectiveSports), Poll, UnwatchedPoll, BreakPoll, _options.MaxFollowedGames, SlowLanePerPoll);

        using var timer = new PeriodicTimer(Poll, _time);

        do
        {
            try
            {
                await PollAsync(_time.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The database restarting, say.
                logger.LogError(ex, "Gamecast poll failed; continuing");
            }
        }
        while (await NextTickAsync(timer, stoppingToken));

        logger.LogInformation("Gamecast feed stopped");
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>One poll: every game in play read and written once. Public so a test can drive the clock.</summary>
    public async Task PollAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The poll's scope holds the one port every request goes through, so its pacing spans the games.
        await using var scope = scopeFactory.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGamecastSource>() is not { } source)
        {
            if (!_noSourceReported)
                logger.LogInformation("Gamecast feed idle: no gamecast source is registered (Ingestion:Espn:Enabled)");

            _noSourceReported = true;
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

        var inPlay = (await IngestionJobs.InPlay(db, now, _options.EffectiveSports)
                .OrderBy(g => g.StartsAt)
                .ThenBy(g => g.Id)
                .Select(g => new { g.Id, Sport = g.Sport!.Key, g.StartsAt, g.ExternalIds })
                .ToListAsync(ct))
            .Where(g => g.ExternalIds.ContainsKey(source.Key))
            .ToList();

        var onNow = inPlay.Select(g => g.Id).ToHashSet();

        // A finished game is forgotten once it has left play here as well, and so is a game in trouble.
        _finished.IntersectWith(onNow);

        foreach (var gone in _trouble.Keys.Where(id => !onNow.Contains(id)).ToList())
            _trouble.Remove(gone);

        // A backed-off game is out of the running until its wait is over — unless a window is
        // watching it, when every poll is its retry. The fast lane takes the watched games first,
        // then the healthy before the ones still in trouble (stable sorts, so earliest start first
        // within each).
        var watched = lanes.Watched(now);

        var candidates = inPlay
            .Where(g => !_finished.Contains(g.Id) && (watched.Contains(g.Id) || !BackedOff(g.Id, now)))
            .OrderBy(g => !watched.Contains(g.Id))
            .ThenBy(g => InTrouble(g.Id))
            .ToList();

        var max = Math.Max(0, _options.MaxFollowedGames);
        var perPoll = SlowLanePerPoll;

        // Every watched game, however many, and the ceiling's slots they leave to the rest. The
        // game someone has open is never the one the ceiling leaves out.
        var watchedOn = candidates.Count(g => watched.Contains(g.Id));
        max = Math.Max(max, watchedOn);

        var fast = candidates.Take(max).ToList();

        // Everything else on, in turn by id. Off, the slow lane holds nothing, as before it existed.
        var slow = perPoll > 0
            ? candidates.Skip(max).OrderBy(g => g.Id).ToList()
            : [];

        if (candidates.Count > max)
        {
            if (!_ceilingReported)
            {
                if (perPoll > 0)
                {
                    logger.LogInformation(
                        "Gamecast feed: {Count} games in play, {Max} read every poll and the rest in the slow lane, {PerPoll} a poll (Ingestion:Gamecast:MaxFollowedGames, SlowLanePerPoll)",
                        candidates.Count, max, perPoll);
                }
                else
                {
                    logger.LogInformation(
                        "Gamecast feed: {Count} games in play, following the first {Max} and not the rest: the slow lane is off (Ingestion:Gamecast:MaxFollowedGames, SlowLanePerPoll)",
                        candidates.Count, max);
                }
            }

            _ceilingReported = true;
        }
        else
        {
            _ceilingReported = false;
        }

        var turn = SlowTurn(slow.Select(g => g.Id).ToList(), perPoll);

        var current = fast.Concat(slow.Where(g => turn.Contains(g.Id)))
            .ToDictionary(g => g.Id, g => _followed.TryGetValue(g.Id, out var known)
                ? known
                : new Followed(g.Sport, g.ExternalIds[source.Key], g.StartsAt));

        // Followed and out of play now: most often the scoreboard pass has just marked it final,
        // and the feed owes it the final summary and the box score. It is read on until the
        // summary agrees, or for LeavingReads polls at most, whichever lane it was in.
        var leaving = _followed.Where(f => !onNow.Contains(f.Key)).ToList();

        // Followed and still in play, but backed off, or past the ceiling with the slow lane off:
        // let go until it is picked again. A slow game waiting its turn keeps what is held for it.
        var held = fast.Concat(slow).Select(g => g.Id).ToHashSet();

        foreach (var displaced in _followed.Keys.Where(id => onNow.Contains(id) && !held.Contains(id)).ToList())
            _followed.Remove(displaced);

        // An unwatched game not yet due — nobody is looking, or it is at a break — waits for a
        // later poll, held as it is. A watched game is read every poll whatever it said last.
        foreach (var (gameId, game) in current.Where(c => !watched.Contains(c.Key) && !Due(c.Value, now)).ToList())
        {
            current.Remove(gameId);
            _followed[gameId] = game;
        }

        // Watched games first, whether in play or leaving it, so the game someone has open is
        // asked before anything else this poll asks; then the rest in lane order, leaving last.
        var reads = current.Select(c => (Id: c.Key, Game: c.Value, Leaving: false))
            .Concat(leaving.Select(l => (Id: l.Key, Game: l.Value, Leaving: true)))
            .OrderBy(r => !watched.Contains(r.Id))
            .ToList();

        foreach (var (gameId, game, isLeaving) in reads)
        {
            ct.ThrowIfCancellationRequested();
            await FollowAsync(source, gameId, game, isLeaving, watched.Contains(gameId), now, ct);
        }

        foreach (var stale in _lastReadAt.Keys.Where(id => !onNow.Contains(id) && !_followed.ContainsKey(id)).ToList())
            _lastReadAt.Remove(stale);

        // The lanes as this poll leaves them: a game it finished is gone, one it just backed off says so.
        var fastIds = fast.Select(g => g.Id).ToHashSet();
        var games = new Dictionary<int, GamecastLane>();

        foreach (var g in inPlay.Where(g => !_finished.Contains(g.Id)))
        {
            if (BackedOff(g.Id, now) && !watched.Contains(g.Id))
                games[g.Id] = GamecastLane.BackedOff;
            else if (fastIds.Contains(g.Id))
                games[g.Id] = GamecastLane.Fast;
            else if (held.Contains(g.Id))
                games[g.Id] = GamecastLane.Slow;
        }

        lanes.Report(new GamecastFeedSnapshot(
            now, inPlay.Count, fast.Count, games, new Dictionary<int, DateTimeOffset>(_lastReadAt)));
    }

    /// <summary>
    /// The slow games read this poll: the next <paramref name="perPoll"/> by id after the last one
    /// read, never wrapping within a poll, so with N slow games each is read once every
    /// N ÷ <paramref name="perPoll"/> polls, rounded up. A game that joins the lane waits its turn.
    /// </summary>
    private HashSet<int> SlowTurn(List<int> slowIds, int perPoll)
    {
        if (perPoll <= 0 || slowIds.Count == 0)
            return [];

        var next = slowIds.Where(id => id > _slowCursor).ToList();

        // The round is over: start it again from the lowest id.
        if (next.Count == 0)
            next = slowIds;

        var turn = next.Take(perPoll).ToList();
        _slowCursor = turn[^1];
        return turn.ToHashSet();
    }

    /// <summary>
    /// Whether an unwatched game's gap has run. Half a poll early counts: the polls fall on a beat
    /// and the gaps are whole polls, so a tick a few milliseconds early must not cost a whole poll.
    /// </summary>
    private bool Due(Followed game, DateTimeOffset now)
        => game.NextReadAt is not { } next || now + Poll / 2 >= next;

    /// <summary>
    /// ESPN's header at a break — "Halftime", "End of 1st" … "End of 4th" — when nothing is played
    /// and the summary barely moves. Read from the short detail, as the window shows it.
    /// </summary>
    internal static bool AtBreak(CanonicalGamecast gamecast)
        => gamecast.State == "live"
           && gamecast.StatusDetail is { } detail
           && (detail.StartsWith("Halftime", StringComparison.OrdinalIgnoreCase)
               || detail.StartsWith("End of", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The gamecast as it would be written, hashed: the plays, the drives and the probabilities laid
    /// on them. The parsed gamecast rather than ESPN's body, because the body streams straight into
    /// the parser and is never held whole (<c>EspnStatsAdapter.GetJsonAsync</c>); hashing it would
    /// mean buffering half a megabyte a read again, and moving the hash into the port. The parse is
    /// the cheap half; the writer's load and compare against the store is the half this skips.
    /// </summary>
    internal static string Fingerprint(CanonicalGamecast gamecast)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(gamecast)));

    private bool InTrouble(int gameId)
        => _trouble.TryGetValue(gameId, out var trouble) && trouble.Failures >= FailuresBeforeBackOff;

    private bool BackedOff(int gameId, DateTimeOffset now)
        => InTrouble(gameId) && now < _trouble[gameId].RetryAt;

    /// <summary>
    /// Reads and writes one game, finishing it if ESPN calls it final. A failure is this game's
    /// alone: it is logged, and a game still in play is asked again next poll, or after its
    /// back-off once it has failed too often.
    /// </summary>
    private async Task FollowAsync(
        IGamecastSource source, int gameId, Followed game, bool leaving, bool watched, DateTimeOffset now, CancellationToken ct)
    {
        CanonicalGamecast? gamecast = null;
        StatsFetchResult? box = null;

        try
        {
            // The game's own requests, marked as a feed read's: asked once with a short timeout, and
            // ahead of every other ESPN request if a window is watching (GamecastRequest).
            using (GamecastRequest.Begin(watched, ReadTimeout))
            {
                // The box score rides on the summary read anyway, parsed when its cadence is due.
                if (game.BoxAt is not { } boxed || now - boxed >= BoxPoll)
                {
                    var read = await source.FetchGamecastWithBoxAsync(game.Sport, game.EventId, ct);
                    gamecast = read?.Gamecast;
                    box = read?.Box;
                }
                else
                {
                    gamecast = await source.FetchGamecastAsync(game.Sport, game.EventId, ct);
                }

                if (gamecast is null)
                {
                    // The port cannot address this league; asking every fifteen seconds will not change that.
                    _followed.Remove(gameId);
                    _trouble.Remove(gameId);
                    return;
                }

                _lastReadAt[gameId] = now;

                // Probabilities only once the game is on: a game not started, or stuck before its start, has none.
                if (gamecast.State == "live" && (game.ProbabilitiesAt is not { } asked || now - asked >= ProbabilityPoll))
                    await RefreshProbabilitiesAsync(source, game, now, ct);
            }

            // Nobody watching: a live game waits UnwatchedPoll, or BreakPoll at a break, before its
            // next read. Anything else — not started, leaving, final — is read again next poll.
            game.NextReadAt = gamecast.State == "live"
                ? now + (AtBreak(gamecast) ? BreakPoll : UnwatchedPoll)
                : null;

            var merged = EspnStatsAdapter.MergeProbabilities(gamecast, game.Probabilities);

            // The same plays, drives and probabilities as the last write that landed: the writer
            // would load the game's rows only to find nothing to save, so it is not asked.
            var fingerprint = Fingerprint(merged);

            if (fingerprint != game.Fingerprint)
            {
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var writer = scope.ServiceProvider.GetRequiredService<GamecastWriter>();
                    var written = await writer.WriteAsync(gameId, source.Key, merged, ct);

                    if (written.Changed)
                    {
                        logger.LogInformation(
                            "Gamecast {GameId}: {Plays} plays in, {Updated} changed, {Drives} drives written",
                            gameId, written.PlaysInserted, written.PlaysUpdated, written.DrivesInserted + written.DrivesUpdated);
                    }
                }

                game.Fingerprint = fingerprint;
            }

            // Live only: a final summary's box is the final path's, written whole with the game.
            // Its own cadence, apart from the fingerprint: a box can move while the plays do not.
            if (gamecast.State == "live" && box is not null)
                await WriteLiveBoxAsync(source, gameId, game, box, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Whatever failed, the next read writes in full and the store decides.
            game.Fingerprint = null;
            game.NextReadAt = null;

            Failed(gameId, game, now, ex);
            gamecast = null;

            if (!leaving)
                return;
        }

        if (gamecast?.State == "final")
        {
            Recovered(gameId, game);
            await FinishAsync(gameId, game, source, summaryFinal: true, ct);
        }
        else if (gamecast?.State == "postponed")
        {
            // Let go for as long as it stays in play here, or it would be taken up every poll.
            _followed.Remove(gameId);
            _finished.Add(gameId);
            _trouble.Remove(gameId);
        }
        else if (leaving)
        {
            if (++game.ReadsSinceLeaving >= LeavingReads)
            {
                await FinishAsync(gameId, game, source, summaryFinal: false, ct);
            }
            else
            {
                _followed[gameId] = game;
            }
        }
        else
        {
            if (gamecast?.State == "scheduled")
                Stalled(gameId, game, now);
            else
                Recovered(gameId, game);

            _followed[gameId] = game;
        }
    }

    /// <summary>
    /// A read that threw. Said once at Warning, with the stack, and at Debug after that until the
    /// game recovers, so a stale event id does not write a stack trace every fifteen seconds.
    /// </summary>
    private void Failed(int gameId, Followed game, DateTimeOffset now, Exception ex)
    {
        var trouble = Note(gameId, now);

        if (trouble.Failures == 1)
            logger.LogWarning(ex, "Gamecast {GameId} ({EventId}): poll failed; continuing", gameId, game.EventId);
        else
            logger.LogDebug("Gamecast {GameId} ({EventId}): poll failed again ({Failures} in a row): {Error}",
                gameId, game.EventId, trouble.Failures, ex.Message);
    }

    /// <summary>A game past its start whose summary still says "scheduled": delayed or suspended.</summary>
    private void Stalled(int gameId, Followed game, DateTimeOffset now)
    {
        var trouble = Note(gameId, now);

        logger.LogDebug("Gamecast {GameId} ({EventId}): not started on ESPN ({Reads} reads in a row)",
            gameId, game.EventId, trouble.Failures);
    }

    /// <summary>Counts a bad read and, past the threshold, sets the next ask a growing while out.</summary>
    private Trouble Note(int gameId, DateTimeOffset now)
    {
        if (!_trouble.TryGetValue(gameId, out var trouble))
            _trouble[gameId] = trouble = new Trouble();

        trouble.Failures++;

        if (trouble.Failures >= FailuresBeforeBackOff)
        {
            // Four polls' worth, doubling: a minute, two, four … at the default cadence, up to the cap.
            var doublings = Math.Min(trouble.Failures - FailuresBeforeBackOff, 10);
            var wait = TimeSpan.FromTicks(Math.Min(Poll.Ticks * 4 * (1L << doublings), MaxBackOff.Ticks));
            trouble.RetryAt = now + wait;

            logger.LogDebug("Gamecast {GameId}: backed off until {RetryAt}", gameId, trouble.RetryAt);
        }

        return trouble;
    }

    private void Recovered(int gameId, Followed game)
    {
        if (_trouble.Remove(gameId, out var trouble) && trouble.Failures >= FailuresBeforeBackOff)
        {
            logger.LogInformation("Gamecast {GameId} ({EventId}): reading again after {Failures} bad reads",
                gameId, game.EventId, trouble.Failures);
        }
    }

    /// <summary>
    /// The core API's rows for the game, kept for the writes until the next ask. A failure keeps
    /// the last rows — the plays they describe have not changed — and waits the cadence out.
    /// </summary>
    private async Task RefreshProbabilitiesAsync(IGamecastSource source, Followed game, DateTimeOffset now, CancellationToken ct)
    {
        game.ProbabilitiesAt = now;

        try
        {
            game.Probabilities = await source.FetchProbabilitiesAsync(game.Sport, game.EventId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gamecast {EventId}: probabilities unavailable; keeping the last", game.EventId);
        }
    }

    /// <summary>
    /// The box score as it stands, from the summary just read, written through the results pass's
    /// path under <see cref="IngestionJobs.EspnGamecastLive"/> — its own job, so a final holding
    /// only these lines is still owed its stats. The players and their lines only — the game's status and
    /// score stay the scoreboard pass's — and quietly: a box that moved no line writes no run. A
    /// failure is logged and waits the cadence out; it does not cost the game its plays.
    /// </summary>
    private async Task WriteLiveBoxAsync(
        IGamecastSource source, int gameId, Followed game, StatsFetchResult box, DateTimeOffset now, CancellationToken ct)
    {
        game.BoxAt = now;

        if (box.PlayerStats.Count == 0)
            return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var stats = scope.ServiceProvider.GetRequiredService<StatsIngestionService>();
            var outcome = await stats.IngestLiveBoxScoreAsync(source.Key, game.Sport, IngestionJobs.EspnGamecastLive, box, ct);

            if (outcome.RunId != 0)
                logger.LogDebug("Gamecast {GameId}: live box score {Status}, {Rows} rows", gameId, outcome.Status, outcome.RowsIngested);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gamecast {GameId} ({EventId}): live box score failed; asked again next cadence",
                gameId, game.EventId);
        }
    }

    /// <summary>
    /// A game over: let go, no drive current any more, and the box score asked for once. Run when
    /// the summary says final, and also when a game that left play never got a final summary in
    /// its <see cref="LeavingReads"/> — then the box score may well come back empty. A box score
    /// that will not come is not retried here — the results sweep owes every final its stats
    /// (<see cref="IngestionJobs.DatesAwaitingResultsAsync"/>) and heals it on its own back-off.
    /// </summary>
    private async Task FinishAsync(
        int gameId, Followed game, IGamecastSource source, bool summaryFinal, CancellationToken ct)
    {
        _followed.Remove(gameId);
        _finished.Add(gameId);
        _trouble.Remove(gameId);

        if (!summaryFinal)
        {
            logger.LogInformation(
                "Gamecast {GameId} ({EventId}): out of play, and the summary never said final in {Reads} reads; finishing it",
                gameId, game.EventId, LeavingReads);
        }

        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();
                var current = await db.GameDrives.Where(d => d.GameId == gameId && d.IsCurrent).ToListAsync(ct);

                if (current.Count > 0)
                {
                    foreach (var drive in current)
                    {
                        drive.IsCurrent = false;
                        drive.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    // Through SaveChanges, not a bulk update, so the window hears plays:{id} and drops the marker.
                    await db.SaveChangesAsync(ct);
                }
            }

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var stats = scope.ServiceProvider.GetRequiredService<StatsIngestionService>();
                var outcome = await stats.IngestBoxScoreAsync(
                    source, game.Sport, JobKey, LeagueClock.DateOf(game.StartsAt), game.EventId, ct);

                logger.LogInformation(
                    "Gamecast {GameId}: final; box score {Status}, {Rows} rows. No longer followed",
                    gameId, outcome.Status, outcome.RowsIngested);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gamecast {GameId} ({EventId}): finishing failed; the results sweep will heal the box score",
                gameId, game.EventId);
        }
    }
}
