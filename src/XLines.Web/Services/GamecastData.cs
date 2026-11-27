using System.Globalization;
using XLines.Data.Changes;
using XLines.Data.CrossReference;
using XLines.Data.Gamecast;
using XLines.Desk.Windowing;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace XLines.Web.Services;

/// <summary>
/// What the Gamecast window reads (spec 2026-10-24 §6.1, §6.4): the header, the plays after the
/// last one it holds, the drives, and the box score once the game has one.
///
/// <para>
/// An interface so the window renders in tests without a database, as the Game window's
/// <see cref="IGameWindowData"/> does. The probability line and the scoring summary are not
/// asked for separately: every play the window holds already carries its probabilities and its
/// scoring flag, so both are drawn from the plays, and an append moves them with no query of
/// their own.
/// </para>
/// </summary>
public interface IGamecastData
{
    /// <summary>The game, the state of play and the pre-game close; null when there is no such game.</summary>
    Task<GamecastHeader?> HeaderAsync(int gameId, CancellationToken ct = default);

    /// <summary>
    /// Every play and drive the reader may see, the drives with their spans, and the watermark to
    /// read changes after. Shared across circuits per game and tier (<see cref="GamecastSnapshots"/>):
    /// <paramref name="settled"/> — the game is final — lets the snapshot live for hours, since only
    /// a notice can change it; otherwise it lives seconds.
    /// </summary>
    Task<GamecastRows> SnapshotAsync(int gameId, bool settled, CancellationToken ct = default);

    /// <summary>
    /// The plays and drives written since <paramref name="since"/> (the last watermark read), in any
    /// drive, and the next watermark; every row when it is null (<see cref="GamecastService.ChangesSinceAsync"/>).
    /// </summary>
    Task<GamecastRows> ChangesSinceAsync(int gameId, DateTimeOffset? since, CancellationToken ct = default);

    /// <summary>The box score, the Game window's own read; null until the game has one.</summary>
    Task<GameBoxScore?> BoxScoreAsync(int gameId, CancellationToken ct = default);

    /// <summary>Why a finished game holds no plays, when the backport knows; null when it has not missed it.</summary>
    Task<GamecastMiss?> BackportMissAsync(int gameId, CancellationToken ct = default);

    /// <summary>
    /// A window is open on this game: the feed in this process reads it first
    /// (<see cref="GamecastLanes.Watch"/>). Called once per window; renewed with <see cref="Renew"/>
    /// and given back with <see cref="Unwatch"/>, exactly once each.
    /// </summary>
    void Watch(int gameId);

    /// <summary>The window is still open on the game: its interest's lease runs on, counted once.</summary>
    void Renew(int gameId);

    /// <summary>The window closed, or the game ended: its interest is given back.</summary>
    void Unwatch(int gameId);

    /// <summary>How the feed in this process is reading the game, or that it does not run here.</summary>
    GamecastFeedReading Feed(int gameId);
}

/// <summary>
/// How the gamecast feed stood on its last poll, for one game, as the window words it.
///
/// <para>
/// <see cref="Here"/> is false on a host whose feed has not polled — the feed runs on the machine
/// that runs the schedule — and then nothing else but the configuration is known. Otherwise
/// <see cref="Lane"/> is the game's lane (null when the feed is not reading it),
/// <see cref="InPlay"/> the games in play, <see cref="FastLaneSize"/> those read every poll and
/// <see cref="SlowGames"/> those read in turn. <see cref="Poll"/> and <see cref="SlowLanePerPoll"/>
/// are the configured pace (<see cref="GamecastOptions"/>). <see cref="Promoted"/> says the game
/// is watched and the watched games fit the fast lane, so its next poll reads it every time.
/// </para>
/// </summary>
public sealed record GamecastFeedReading(
    bool Here,
    GamecastLane? Lane,
    int InPlay,
    int FastLaneSize,
    int SlowGames,
    TimeSpan Poll,
    int SlowLanePerPoll,
    bool Promoted)
{
    /// <summary>The feed does not run in this process: only the configured pace is known.</summary>
    public static GamecastFeedReading NotHere(TimeSpan poll, int slowLanePerPoll)
        => new(false, null, 0, 0, 0, poll, slowLanePerPoll, false);

    /// <summary>
    /// How often a game in turn is read: ceil(slow games / per poll) polls. Null when the turn is
    /// off (<see cref="SlowLanePerPoll"/> zero).
    /// </summary>
    public TimeSpan? SlowEvery => SlowLanePerPoll <= 0
        ? null
        : Poll * Math.Ceiling(Math.Max(1, SlowGames) / (double)SlowLanePerPoll);
}

/// <summary>
/// The seam over <see cref="GamecastService"/> and <see cref="GameLogService"/>, and over the
/// feed's lanes (<see cref="GamecastLanes"/>, a singleton of the ingestion library, resolvable
/// where the feed does not run). The gamecast service takes a context per call from the factory;
/// the box score's reader is scoped, so it gets a scope per call, as <see cref="GameWindowData"/> does.
/// </summary>
public sealed class GamecastData(
    GamecastService gamecast,
    GamecastSnapshots snapshots,
    IServiceScopeFactory scopes,
    GamecastLanes lanes,
    IOptions<IngestionOptions> options) : IGamecastData
{
    public void Watch(int gameId) => lanes.Watch(gameId, DateTimeOffset.UtcNow);

    public void Renew(int gameId) => lanes.Renew(gameId, DateTimeOffset.UtcNow);

    public void Unwatch(int gameId) => lanes.Unwatch(gameId);

    public GamecastFeedReading Feed(int gameId)
    {
        var pace = options.Value.Gamecast;

        // Read once: the feed replaces the snapshot after every poll.
        if (lanes.Snapshot is not { } snapshot)
            return GamecastFeedReading.NotHere(pace.Poll, pace.SlowLanePerPoll);

        var watched = lanes.Watched(DateTimeOffset.UtcNow);

        return new GamecastFeedReading(
            Here: true,
            Lane: snapshot.Games.TryGetValue(gameId, out var lane) ? lane : null,
            InPlay: snapshot.InPlay,
            FastLaneSize: snapshot.FastLaneSize,
            SlowGames: snapshot.Games.Values.Count(l => l == GamecastLane.Slow),
            Poll: pace.Poll,
            SlowLanePerPoll: pace.SlowLanePerPoll,
            Promoted: watched.Contains(gameId) && watched.Count <= pace.MaxFollowedGames);
    }

    public Task<GamecastHeader?> HeaderAsync(int gameId, CancellationToken ct = default)
        => gamecast.HeaderAsync(gameId, ct);

    /// <summary>
    /// The shared snapshot, filed under this reader's visible sources: the key is computed here, for
    /// this circuit's tier, and the load reads with exactly that answer, so a snapshot is only ever
    /// handed to a reader who would have read the same rows. The load runs without this caller's
    /// token: another window may be waiting on it.
    /// </summary>
    public async Task<GamecastRows> SnapshotAsync(int gameId, bool settled, CancellationToken ct = default)
    {
        var visible = await gamecast.VisibleSourcesAsync(ct);

        return await snapshots.GetAsync(gameId, GamecastService.TierKey(visible), settled,
            () => gamecast.SnapshotAsync(gameId, visible, CancellationToken.None)).WaitAsync(ct);
    }

    public Task<GamecastRows> ChangesSinceAsync(int gameId, DateTimeOffset? since, CancellationToken ct = default)
        => gamecast.ChangesSinceAsync(gameId, since, ct);

    public Task<GamecastMiss?> BackportMissAsync(int gameId, CancellationToken ct = default)
        => gamecast.BackportMissAsync(gameId, ct);

    public async Task<GameBoxScore?> BoxScoreAsync(int gameId, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GameLogService>().BoxScoreAsync(gameId, ct);
    }
}

/// <summary>
/// One game's plays and drives, read once and handed to every window open on it, whichever
/// circuit (spec 2026-10-24 §6.4): thirteen operators on one Sunday final should cost one read,
/// not thirteen, and a window reopened on a final should cost none.
///
/// <para>
/// <b>Keyed</b> by the game and the reader's tier (<see cref="GamecastService.TierKey"/>, the
/// sources the reader may see, ADR 0024): a visitor is never handed the operator's rows, nor the
/// operator a visitor's empty answer. The header is not held here: it carries the reader's close
/// and its personal-use columns are blanked per reader, and it is one cheap row besides.
/// </para>
///
/// <para>
/// <b>Evicted</b> by the change stream every window hears (<see cref="DeskSignals"/>, fed by the
/// database's notices): <c>plays:{id}</c> or <c>games:{id}</c> drops that game's entries, every
/// tier; a bare <c>plays</c> or <c>games</c> — the listener's fallback poll, its "publish all" on
/// reconnecting, a save too large to address — drops every entry, since it cannot say which game
/// moved. Plays and drives are written only by tracked saves (see <c>GamePlay</c>), so no write
/// reaches the tables without a notice. A final's entry lives <see cref="SettledFor"/>, sliding,
/// since only a notice changes it; any other game's at most <see cref="LiveFor"/>, a bound on what a
/// lost notice could cost — a live window advances past its snapshot by its own watermark reads.
/// </para>
///
/// <para>
/// <b>One load</b> per entry: the first caller's load is the entry, and a caller arriving while it
/// is in flight awaits the same task. A load that fails is dropped, so the next caller tries again.
/// An entry evicted while its load is in flight is not put back; whoever awaited it got rows no
/// newer than the notice, and the notice itself makes each open window read past them.
/// </para>
///
/// <para>
/// Its own <see cref="MemoryCache"/> rather than the host's, bounded to <see cref="Capacity"/>
/// entries: a size limit on a shared cache would refuse every other user's unsized entry.
/// </para>
/// </summary>
public sealed class GamecastSnapshots : IDisposable
{
    /// <summary>The most snapshots held at once; past it the cache compacts its oldest.</summary>
    public const int Capacity = 64;

    /// <summary>How long a final's snapshot is kept without being asked for.</summary>
    public static readonly TimeSpan SettledFor = TimeSpan.FromHours(2);

    /// <summary>The longest any other game's snapshot is kept, notice or none.</summary>
    public static readonly TimeSpan LiveFor = TimeSpan.FromSeconds(30);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = Capacity });
    private readonly DeskSignals _signals;

    /// <summary>Guards the find-or-add, so two opens of one game share one load, and <see cref="_tiers"/>.</summary>
    private readonly Lock _gate = new();

    /// <summary>The tiers each game has been filed under, so a game's notice can drop every one of them.</summary>
    private readonly Dictionary<int, HashSet<string>> _tiers = [];

    public GamecastSnapshots(DeskSignals signals)
    {
        _signals = signals;
        _signals.Changed += OnChanged;
    }

    /// <summary>
    /// The snapshot for the game and tier: held, in flight, or loaded now by <paramref name="load"/>.
    /// </summary>
    public Task<GamecastRows> GetAsync(int gameId, string tier, bool settled, Func<Task<GamecastRows>> load)
    {
        var key = new Key(gameId, tier);
        Task<GamecastRows> task;

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out Task<GamecastRows>? held) && held is not null && !held.IsFaulted && !held.IsCanceled)
                return held;

            // Started under the lock and stored before it is released: a second caller finds the
            // task, never an empty slot. On the pool, off any one circuit's dispatcher, since
            // every window waiting on it may outlive the one that asked first.
            task = Task.Run(load);

            var options = new MemoryCacheEntryOptions { Size = 1 };
            if (settled)
                options.SlidingExpiration = SettledFor;
            else
                options.AbsoluteExpirationRelativeToNow = LiveFor;

            _cache.Set(key, task, options);

            if (!_tiers.TryGetValue(gameId, out var tiers))
                _tiers[gameId] = tiers = [];
            tiers.Add(tier);
        }

        return Forget(key, task);
    }

    /// <summary>A load that failed is not kept: the next caller asks again.</summary>
    private async Task<GamecastRows> Forget(Key key, Task<GamecastRows> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out Task<GamecastRows>? held) && ReferenceEquals(held, task))
                    _cache.Remove(key);
            }

            throw;
        }
    }

    /// <summary>Drops every tier's snapshot of one game.</summary>
    public void Evict(int gameId)
    {
        lock (_gate)
        {
            if (!_tiers.Remove(gameId, out var tiers))
                return;

            foreach (var tier in tiers)
                _cache.Remove(new Key(gameId, tier));
        }
    }

    /// <summary>Drops every snapshot.</summary>
    public void EvictAll()
    {
        lock (_gate)
        {
            _tiers.Clear();
            _cache.Clear();
        }
    }

    /// <summary>How many snapshots are held: for tests.</summary>
    public int Count => _cache.Count;

    private void OnChanged(IReadOnlySet<string> tokens)
    {
        List<int>? games = null;

        foreach (var token in tokens)
        {
            if (token is DataTopics.Plays or DataTopics.Games)
            {
                EvictAll();
                return;
            }

            if ((GameOf(token, DataTopics.Plays) ?? GameOf(token, DataTopics.Games)) is { } id)
                (games ??= []).Add(id);
        }

        if (games is null)
            return;

        foreach (var id in games)
            Evict(id);
    }

    /// <summary>The game a <c>topic:{id}</c> token addresses, or null for any other token.</summary>
    private static int? GameOf(string token, string topic)
        => token.Length > topic.Length + 1
           && token.StartsWith(topic, StringComparison.Ordinal)
           && token[topic.Length] == DeskSignals.AddressSeparator
           && int.TryParse(token.AsSpan(topic.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    public void Dispose()
    {
        _signals.Changed -= OnChanged;
        _cache.Dispose();
    }

    private readonly record struct Key(int GameId, string Tier);
}
