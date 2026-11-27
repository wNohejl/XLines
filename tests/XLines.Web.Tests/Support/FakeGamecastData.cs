using XLines.Core.Entities;
using XLines.Data.CrossReference;
using XLines.Data.Gamecast;
using XLines.Web.Services;

namespace XLines.Web.Tests.Support;

/// <summary>
/// The Gamecast window's data seam, in memory: a header, the plays and drives a test sets, and a
/// record of every read — the snapshots, and the watermark each change read asked after — so a
/// test can say whether a notice appended, corrected or reloaded. Every row is stamped as the
/// writer stamps it when it is new or differs from what was read last (<c>Stamp</c>), so the
/// change reads return what moved, in any drive, as the database does.
///
/// <para>
/// <see cref="Gate"/> holds every plays read until the test releases it, so two loads really
/// interleave: both are past their first await before either has its plays.
/// </para>
/// </summary>
public sealed class FakeGamecastData : IGamecastData
{
    public const int HomeId = 1;
    public const int AwayId = 2;

    public GamecastHeader? Header { get; set; } = Live();

    /// <summary>When set, the header read throws it.</summary>
    public Exception? HeaderError { get; set; }

    /// <summary>When set, every plays read throws it.</summary>
    public Exception? PlaysError { get; set; }

    public List<GamecastPlay> Plays { get; } = [];

    public List<GamecastDrive> Drives { get; } = [];

    public GameBoxScore? Box { get; set; }

    /// <summary>When set, every plays read waits on it before answering from <see cref="Plays"/>.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Plays reads that have reached the gate and not yet passed it.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    private int _waiting;

    public int HeaderReads { get; private set; }
    public int BoxReads { get; private set; }

    /// <summary>Snapshot reads: every play and drive, as a window's open asks.</summary>
    public int SnapshotReads { get; private set; }

    /// <summary>The <c>settled</c> each snapshot read was asked with, in order.</summary>
    public List<bool> SnapshotSettled { get; } = [];

    /// <summary>The watermark every change read asked after, in order; null for one asking every row.</summary>
    public List<DateTimeOffset?> ChangeReadsSince { get; } = [];

    /// <summary>Every read of plays and drives, snapshots and changes alike.</summary>
    public int RowReads => SnapshotReads + ChangeReadsSince.Count;

    public Task<GamecastHeader?> HeaderAsync(int gameId, CancellationToken ct = default)
    {
        HeaderReads++;
        return HeaderError is { } error ? Task.FromException<GamecastHeader?>(error) : Task.FromResult(Header);
    }

    // ---- The stamps: a row's UpdatedAt, as the writer would have set it ---------------------

    /// <summary>Where the stamps start: any fixed instant, so a watermark never reads this machine's clock.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    private long _ticks;
    private readonly Dictionary<long, (GamecastPlay Row, DateTimeOffset At)> _playStamps = [];
    private readonly Dictionary<long, (GamecastDrive Row, DateTimeOffset At)> _driveStamps = [];

    /// <summary>
    /// Stamps every play and drive that is new or differs from what was last stamped, a second
    /// apart, the way the writer stamps a row it inserts or changes; so a test edits
    /// <see cref="Plays"/> and <see cref="Drives"/> as it always has and the change reads find it.
    /// </summary>
    private void Stamp()
    {
        foreach (var play in Plays)
        {
            if (!_playStamps.TryGetValue(play.Id, out var held) || held.Row != play)
                _playStamps[play.Id] = (play, Epoch.AddSeconds(++_ticks));
        }

        foreach (var drive in Drives)
        {
            var key = DriveKey(drive);
            if (!_driveStamps.TryGetValue(key, out var held) || held.Row != drive)
                _driveStamps[key] = (drive, Epoch.AddSeconds(++_ticks));
        }
    }

    private static long DriveKey(GamecastDrive d) => d.Id != 0 ? d.Id : -1L - d.Sequence;

    /// <summary>The newest stamp held, null before any row.</summary>
    private DateTimeOffset? Newest()
        => _playStamps.Values.Select(s => (DateTimeOffset?)s.At).Concat(_driveStamps.Values.Select(s => (DateTimeOffset?)s.At)).Max();

    public async Task<GamecastRows> SnapshotAsync(int gameId, bool settled, CancellationToken ct = default)
    {
        SnapshotReads++;
        SnapshotSettled.Add(settled);
        await PassAsync();

        Stamp();
        var plays = Ordered(Plays);
        return new GamecastRows(plays, GamecastDrives.WithSpans(Drives.OrderBy(d => d.Sequence).ToList(), plays), Newest());
    }

    /// <summary>
    /// The rows stamped at or after <paramref name="since"/> — the service's own rule, less its
    /// overlap, which a second's stamps never need — and the newest stamp as the next watermark.
    /// </summary>
    public async Task<GamecastRows> ChangesSinceAsync(int gameId, DateTimeOffset? since, CancellationToken ct = default)
    {
        ChangeReadsSince.Add(since);
        await PassAsync();

        Stamp();
        var plays = Ordered(Plays.Where(p => since is not { } at || _playStamps[p.Id].At >= at));
        var drives = Drives.Where(d => since is not { } at || _driveStamps[DriveKey(d)].At >= at).OrderBy(d => d.Sequence).ToList();
        var newest = Newest();

        return new GamecastRows(plays, drives, since is { } s && newest is { } n && s > n ? s : newest ?? since);
    }

    private static List<GamecastPlay> Ordered(IEnumerable<GamecastPlay> plays)
        => plays.OrderBy(p => p.Sequence).ThenBy(p => p.Id).ToList();

    private async Task PassAsync()
    {
        if (PlaysError is { } error)
            throw error;

        if (Gate is { } gate)
        {
            Interlocked.Increment(ref _waiting);
            await gate.Task;
            Interlocked.Decrement(ref _waiting);
        }
    }

    public Task<GameBoxScore?> BoxScoreAsync(int gameId, CancellationToken ct = default)
    {
        BoxReads++;
        return Task.FromResult(Box);
    }

    /// <summary>What <see cref="BackportMissAsync"/> answers: by default the backport has missed nothing.</summary>
    public GamecastMiss? Miss { get; set; }

    public int MissReads { get; private set; }

    public Task<GamecastMiss?> BackportMissAsync(int gameId, CancellationToken ct = default)
    {
        MissReads++;
        return Task.FromResult(Miss);
    }

    /// <summary>What <see cref="Feed"/> answers: by default the feed does not run in this process.</summary>
    public GamecastFeedReading Reading { get; set; } = GamecastFeedReading.NotHere(TimeSpan.FromSeconds(15), 1);

    public GamecastFeedReading Feed(int gameId) => Reading;

    private readonly Lock _interestGate = new();
    private readonly List<string> _interest = [];

    /// <summary>Every interest call, in order: "watch:{id}", "renew:{id}", "unwatch:{id}".</summary>
    public IReadOnlyList<string> Interest
    {
        get
        {
            lock (_interestGate)
                return _interest.ToList();
        }
    }

    public int Count(string call) => Interest.Count(c => c.StartsWith(call + ":", StringComparison.Ordinal));

    public void Watch(int gameId) => Record($"watch:{gameId}");

    public void Renew(int gameId) => Record($"renew:{gameId}");

    public void Unwatch(int gameId) => Record($"unwatch:{gameId}");

    // The window renews from a timer as well as on its notices, off the renderer's thread.
    private void Record(string call)
    {
        lock (_interestGate)
            _interest.Add(call);
    }

    /// <summary>PHI at WSH, live in the second quarter: PHI's ball, 2nd and 6 at WSH 34 — 34 yards from the home goal.</summary>
    public static GamecastHeader Live(string sport = "nfl", GameStatus status = GameStatus.Live) => new(
        GameId: BoardRows.GameId,
        SportKey: sport,
        SportName: sport.ToUpperInvariant(),
        Home: new GamecastTeam(HomeId, "Washington Commanders", "WSH"),
        Away: new GamecastTeam(AwayId, "Philadelphia Eagles", "PHI"),
        StartsAt: DateTimeOffset.UtcNow.AddMinutes(-40),
        Status: status,
        HomeScore: 3,
        AwayScore: 7,
        Period: 2,
        DisplayClock: "4:12",
        ClockSeconds: 252,
        StatusDetail: "4:12 - 2nd",
        PossessionTeamId: AwayId,
        Down: 2,
        Distance: 6,
        YardLine: 34,
        DownDistanceText: "2nd & 6 at WSH 34",
        PossessionText: "WSH 34",
        IsRedZone: false,
        HomeTimeouts: 3,
        AwayTimeouts: 2,
        LastPlay: "J.Hurts pass short right to A.Brown for 9 yards",
        HomeLinescore: [3],
        AwayLinescore: [7],
        Diamond: null,
        LiveUpdatedAt: DateTimeOffset.UtcNow,
        Close: new GamecastClose(3m, 44.5m, true));

    public static GamecastPlay Play(long sequence, int? drive, string text, bool scoring = false, double? win = null, long? id = null)
        => new(
            Id: id ?? 1000 + sequence, Sequence: sequence, DriveSeq: drive, Period: 2, Clock: "5:00", ClockSeconds: 300,
            Type: "Pass", Text: text, HomeScore: 3, AwayScore: 7, IsScoring: scoring, IsTurnover: false,
            IsPenalty: false, Yardage: 5, TeamId: AwayId, Start: null, End: null,
            HomeWinPct: win, SpreadCoverPctHome: null, TotalOverPct: null,
            Wallclock: DateTimeOffset.UtcNow, Modified: null);

    public static GamecastDrive Drive(int sequence, long first, long last, bool current, string? result = null,
        int? plays = null, int? yards = null, string? elapsed = null, bool score = false)
        => new(sequence, AwayId, "PHI", null, result, yards, plays, elapsed, score, current,
            2, "15:00", 75, null, null, null, (int)(last - first + 1), first, last);
}
