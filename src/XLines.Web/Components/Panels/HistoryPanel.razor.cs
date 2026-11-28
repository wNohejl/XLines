using Microsoft.AspNetCore.Components;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.Changes;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Ingestion.Services;
using XLines.Reliability;
using XLines.Web.Services;

namespace XLines.Web.Components.Panels;

/// <summary>
/// The History window's state, its load and the walks it starts: the season coverage's cadence,
/// the range a Backport walks and why each key waits. The markup is <c>HistoryPanel.razor</c>.
/// </summary>
public partial class HistoryPanel : PanelBase
{
    [Inject] private IHistoryData Data { get; set; } = default!;
    [Inject] private HistoryBackfillService Backfill { get; set; } = default!;
    [Inject] private BackfillCoordinator Coordinator { get; set; } = default!;

    protected override IReadOnlySet<string> Watches { get; } = new HashSet<string> { DataTopics.Runs };

    // A runs notice and the coordinator's own Changed (heard empty) both mean a run moved: its
    // progress, checkpoints and coverage count. The season table is whole-database work, so it is
    // left to the cadence in SeasonsDue rather than redone on every one-second settle.
    protected override Task RefreshAsync(IReadOnlySet<string> heard) => LoadAsync(seasons: false);

    protected override string LoadSubject => "The history record";

    /// <summary>The id the Run card's bar labels it by; one window's, never another's.</summary>
    private readonly string _uid = "hist-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// How long the season coverage may stand while runs keep arriving. Thirty seconds: the table
    /// is a summary an operator glances at during a walk, a day walked moves it by a handful of
    /// games, and the walk's end re-reads it at once, so the figure left standing is exact.
    /// </summary>
    public static readonly TimeSpan SeasonsInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a load re-reads the season coverage: when asked for (opening, Re-walk, Try again
    /// after a failure), never yet read, the walk just ended, or the last read is
    /// <see cref="SeasonsInterval"/> old. Otherwise a load reads only what a run changes.
    /// </summary>
    public static bool SeasonsDue(bool asked, DateTimeOffset? lastRead, DateTimeOffset now, bool walkEnded)
        => asked || walkEnded || lastRead is not { } at || now - at >= SeasonsInterval;

    // True until a load finishes, so a load that fails leaves the next one — Try again repeats it —
    // reading everything again, as it did before the season table had a cadence.
    private bool _seasonsAsked = true;
    private DateTimeOffset? _seasonsReadAt;
    private bool _wasWalking;

    /// <summary>A refresh is already set for when the skipped season read falls due.</summary>
    private volatile bool _seasonsCatchUp;

    private IReadOnlyList<SourceEligibility> _eligibility = [];
    private BackfillCoverage? _coverage;
    private List<BackfillCheckpoint> _recent = [];
    private IReadOnlyList<SeasonCoverage> _seasons = [];

    protected override async Task OnInitializedAsync()
    {
        Coordinator.Changed += OnCoordinatorChanged;
        await LoadAsync();
    }

    // The coordinator raises Changed from its own worker thread on every day it walks, so a
    // backfill is a burst of them. They go through the same second's settle as the database's
    // change notices — one reload per burst, on the renderer's context — rather than one read
    // of the whole record per day walked.
    private void OnCoordinatorChanged() => RequestRefresh();

    /// <summary>
    /// The load in flight, cancelled by the next. A walk sends a notice a second and each starts a
    /// load, so a slower read must neither run on beside the newer one nor land after it.
    /// </summary>
    private readonly LatestRead _reads = new();

    /// <summary>
    /// A load claimed the season read and was superseded before it handed it over: the next load
    /// reads the seasons in its place, or the claim would leave the table unread for an interval.
    /// </summary>
    private bool _seasonsPending;

    /// <summary>What one load read, handed over at once.</summary>
    private async Task LoadAsync(bool seasons = true)
    {
        _seasonsAsked |= seasons;

        await WithActivityAsync(async () =>
        {
            var ct = _reads.Next();
            var walking = Coordinator.IsRunning;
            var now = DateTimeOffset.UtcNow;
            var readSeasons = SeasonsDue(_seasonsAsked || _seasonsPending, _seasonsReadAt, now, walkEnded: _wasWalking && !walking);
            var heldSince = _seasonsReadAt;

            // Claimed as the read starts, not as it ends. Notices arrive every second during a
            // walk and each starts a load beside this one: while the season read was slow — the
            // case the cadence is for — every one of them saw it still asked for and read it too.
            _seasonsAsked = false;
            _seasonsPending = readSeasons;

            if (readSeasons)
                _seasonsReadAt = now;

            try
            {
                await LatestRead.HandOverAsync(ct, () => ReadAsync(readSeasons, ct), HandOver);
            }
            catch when (!ct.IsCancellationRequested)
            {
                // A load that failed leaves the next one — Try again repeats it — reading everything.
                _seasonsAsked = true;
                _seasonsReadAt = heldSince;
                throw;
            }

            // Superseded: the newer load carries on from here.
            if (ct.IsCancellationRequested)
                return;

            _wasWalking = walking;

            // Skipped this time: the last notice of a walk can land inside the interval, and with
            // no later one the table would stand as it was for as long as the window stayed open
            // (a walk run by the Worker never shows here as ended). One refresh is asked for at
            // the moment the read falls due.
            if (!readSeasons && !_seasonsCatchUp && _seasonsReadAt is { } read)
            {
                _seasonsCatchUp = true;
                var wait = read + SeasonsInterval - now;
                _ = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero).ContinueWith(_ =>
                {
                    _seasonsCatchUp = false;
                    RequestRefresh();
                }, TaskScheduler.Default);
            }
        },
        settle: () => Coordinator.IsRunning ? PulseState.Active
            : _coverage is { Failed: > 0 } ? PulseState.Warn
            : _coverage is { Completed: > 0 } ? PulseState.Healthy
            : PulseState.Idle,
        status: () => Coordinator.IsRunning
            ? $"{Coordinator.Progress?.Walked ?? 0:N0} days"
            : _coverage is { Completed: > 0 } ? $"{_coverage.Completed:N0} days held" : "no history");
    }

    private Task<HistoryRead> ReadAsync(bool readSeasons, CancellationToken ct) => Data.ReadAsync(readSeasons, ct);

    private void HandOver(HistoryRead read)
    {
        _eligibility = read.Eligibility;
        _coverage = read.Coverage;
        _recent = [.. read.Recent];

        if (read.Seasons is { } seasons)
            _seasons = seasons;

        _sourceNames = read.SourceNames;
        _seasonsPending = false;
    }

    private IReadOnlyDictionary<string, string> _sourceNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A source by the name the registry gives it — "ESPN (unofficial)", as Ops and Runs show it —
    /// rather than the configuration key the backfill reports in. A key the registry does not
    /// hold is shown as itself, since that is exactly the source the row says is not registered.
    /// </summary>
    private string SourceName(string key) => _sourceNames.TryGetValue(key, out var name) ? name : key;

    /// <summary>A league by its abbreviation; a pass named after one ("ufc fighters") keeps the rest in words.</summary>
    private static string League(string sportKey)
    {
        var space = sportKey.IndexOf(' ');
        return space < 0
            ? sportKey.ToUpperInvariant()
            : sportKey[..space].ToUpperInvariant() + sportKey[space..];
    }

    /// <summary>A day as the desk writes one with its year: "Sep 4, 2025", never ISO.</summary>
    private static string LongDay(DateOnly day) => day.ToString("MMM d, yyyy");

    /// <summary>Where the walk is: the source, the league and the day it is fetching.</summary>
    private string WalkingAt => Coordinator.Progress is { } p
        ? $"{League(p.SportKey)}, {LongDay(p.Date)}, from {SourceName(p.SourceKey)}"
        : "Starting…";

    /// <summary>Days the record holds, which is what a re-walk would walk again.</summary>
    private int HeldDays => _coverage?.Completed ?? 0;

    private bool _confirmRewalk;

    /// <summary>
    /// Clears the held-day bookkeeping and starts a fresh walk.
    ///
    /// This is the operation that previously required editing the database by hand: the walk
    /// skips days it has already recorded, so a day stored by code that has since been fixed
    /// could never be corrected from inside the app.
    /// </summary>
    private async Task RewalkAsync()
    {
        _confirmRewalk = false;
        _runError = null;

        try
        {
            await Backfill.ForgetAsync(null, CancellationToken.None);
            await LoadAsync();

            Coordinator.TryStart();
        }
        catch (Exception ex)
        {
            // Caught here because nothing above this handler will: an exception out of an event
            // handler ends the circuit, and the desk with it.
            Log.LogError(ex, "The re-walk did not start");
            _runError = WalkDidNotStart;
        }
    }

    private void Start()
    {
        _runError = null;

        try
        {
            Coordinator.TryStart();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The walk did not start");
            _runError = WalkDidNotStart;
        }
    }

    private const string WalkDidNotStart = "The walk did not start. Try again.";

    // The range a Backport walks: every backfill sport and the last week, until changed.
    private HashSet<string>? _rangeSportsSet;
    private HashSet<string> _rangeSports => _rangeSportsSet ??= new HashSet<string>(Backfill.Sports, StringComparer.OrdinalIgnoreCase);
    private string _rangeFrom = LeagueClock.Today().AddDays(-7).ToString("yyyy-MM-dd");
    private string _rangeTo = LeagueClock.Today().ToString("yyyy-MM-dd");

    private void ToggleRangeSport(string sport, bool on)
    {
        if (on)
            _rangeSports.Add(sport);
        else
            _rangeSports.Remove(sport);
    }

    /// <summary>From the earliest chosen sport's season start (Backfill:Seasons) to today.</summary>
    private void SeasonToDate()
    {
        var chosen = _rangeSports.Count > 0 ? _rangeSports : Backfill.Sports.ToHashSet();
        _rangeFrom = chosen.Select(Backfill.SeasonStart).Min().ToString("yyyy-MM-dd");
        _rangeTo = LeagueClock.Today().ToString("yyyy-MM-dd");
    }

    private static DateOnly? Day(string? text)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", out var day) ? day : null;

    /// <summary>Why Gather history waits, or null when a provider can walk.</summary>
    private string? GatherWaits => _eligibility.Any(s => s.Eligible)
        ? null
        : "No unmetered provider is available to walk — see Sources above.";

    /// <summary>Why Backport range waits: no range named yet, or nothing to walk it with.</summary>
    private string? RangeWaits => RangeAsked is null
        ? "Choose a sport and a From no later than To."
        : GatherWaits;

    /// <summary>The range as asked, or null while it is not one: no sport, a missing day, or From after To.</summary>
    private BackfillRange? RangeAsked
        => _rangeSports.Count > 0 && Day(_rangeFrom) is { } from && Day(_rangeTo) is { } to && from <= to
            ? new BackfillRange(Backfill.Sports.Where(_rangeSports.Contains).ToList(), from, to)
            : null;

    /// <summary>What the range will walk, in words: "MLB and NFL over 34 days, held days included".</summary>
    private string RangeSummary => RangeAsked is { } r
        ? $"{Joined(r.Sports.Select(League).ToList())} over {r.To.DayNumber - r.From.DayNumber + 1:N0} {(r.From == r.To ? "day" : "days")}, held days included"
        : "Choose a sport and dates";

    /// <summary>A list as a sentence says it: "MLB", "MLB and NFL", "MLB, NBA and NFL".</summary>
    private static string Joined(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
    };

    private void BackportRange()
    {
        _runError = null;

        if (RangeAsked is not { } range)
            return;

        try
        {
            if (!Coordinator.TryStart(range: range))
                _runError = "A walk is already running. Stop it first, or wait for it to finish.";
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The range walk did not start");
            _runError = WalkDidNotStart;
        }
    }

    /// <summary>Why the last press of Gather history or Re-walk did nothing, until the next press.</summary>
    private string? _runError;

    private void Stop() => Coordinator.Stop();

    protected override void OnDispose()
    {
        Coordinator.Changed -= OnCoordinatorChanged;
        _reads.Cancel();
    }
}
