using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.Changes;
using XLines.Data.CrossReference;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Web.Components.Desk;
using XLines.Web.Components.Snippets;
using XLines.Web.Services;
using XLines.Web.Windowing;

namespace XLines.Web.Components.Panels;

/// <summary>
/// The Board's state, its load and its filters: what the panel reads, holds and narrows. The
/// open row and its takes are <c>BoardPanel.Rows.cs</c>; the words the board shows are
/// <c>BoardPanel.Text.cs</c>; the markup is <c>BoardPanel.razor</c>.
/// </summary>
public partial class BoardPanel : PanelBase
{
    [Inject] private IBoardData Data { get; set; } = default!;
    [Inject] private IOptions<IngestionOptions> IngestionOptions { get; set; } = default!;
    [Inject] private XLines.Web.Services.WagerSlip Slip { get; set; } = default!;
    [Inject] private DeskToasts Toasts { get; set; } = default!;
    [Inject] private RangePull Ranges { get; set; } = default!;

    protected override IReadOnlySet<string> Watches { get; } = new HashSet<string> { DataTopics.Games, DataTopics.Odds, DataTopics.Alerts };

    /// <summary>
    /// A notice of alerts alone recounts the alerts and nothing else: an alert cannot change a
    /// row, and the whole board — seven to twelve reads — used to be read again every time Ops
    /// raised or resolved one. Anything else heard, or a refresh that does not say what changed
    /// (an empty set), reloads the board as before.
    /// </summary>
    protected override Task RefreshAsync(IReadOnlySet<string> heard)
        => NoticeTokens.OnlyOf(heard, DataTopics.Alerts)
            ? RecountAlertsAsync()
            : LoadAsync();

    protected override string LoadSubject => "The board";

    /// <summary>Which part of the day a reader wants on screen.</summary>
    private enum BoardPhase { Any, Upcoming, Live, Played }

    /// <summary>One league on the filter, with what it holds right now.</summary>
    private record SportOption(string Key, string Label);

    /// <summary>
    /// How far back the board reaches: to the start of today, in the leagues' own day.
    ///
    /// The board is today's slate. A fixed reach of fourteen hours held "how did tonight go"
    /// in the evening, and last night's finals on the following morning — yesterday's game
    /// sitting at the top of today's board. The Journal and History windows are for looking
    /// back; the board stops at midnight — Eastern, the scoreboard's midnight, not the host's,
    /// so a container in UTC does not drop tonight's late games at 8pm.
    /// </summary>
    private static TimeSpan Lookback
        => DateTimeOffset.UtcNow - LeagueClock.StartOf(LeagueClock.Today());

    private static readonly IReadOnlyList<DeskSwitchOption<int>> Horizons =
    [
        new(12, "12h", "Next 12 hours"),
        new(24, "24h", "Next 24 hours"),
        new(48, "48h", "Next 48 hours"),
        // A fight card is billed and bet all week, and is the next thing on the board for days
        // at a time (ADR 0021).
        new(168, "7d", "Next 7 days")
    ];

    private List<BoardRow> _rows = [];
    private List<BoardRow> _visible = [];
    private List<SportOption> _sportOptions = [];
    private List<DeskSwitchOption<BoardPhase>> _phaseOptions = [];
    private readonly FollowUpLauncher _followUps = new();
    private DeskJobMenu? _pull;
    private BoardRow? _open;

    /// <summary>
    /// The open row's snippet, which its strip hands over to be drawn beneath it in the row under
    /// the game. Cleared with the row, so reopening it never shows a stale one.
    /// </summary>
    private RenderFragment? _snippet;

    private string _sport = string.Empty;
    private BoardPhase _phase = BoardPhase.Any;
    private string _team = string.Empty;

    private void SetTeam(string? team)
    {
        _team = team ?? string.Empty;
        ApplyFilter();
    }

    private bool _valueOnly;
    private int _valueRows;

    /// <summary>Narrows to the games with value, most first.</summary>
    private void ToggleValue()
    {
        _valueOnly = !_valueOnly;
        ApplyFilter();
    }
    private int _windowHours = 24;
    private int _bookCount;
    private int _fewestBooks;
    private int _priced;
    private int _unpriced;

    /// <summary>The reason every unpriced row gives, when they all give the same one.</summary>
    private string? SharedGap
    {
        get
        {
            var reasons = _rows.Where(r => r.Unpriced is not null).Select(r => r.Unpriced!).Distinct().Take(2).ToList();
            return reasons.Count == 1 ? reasons[0] : null;
        }
    }
    private int _closing;
    private long _movesToday;
    private int _alerts;
    private int _criticalAlerts;
    private TimeSpan? _oldest;

    /// <summary>
    /// The board under the current phase filter.
    ///
    /// <para>
    /// Held as a field and rebuilt only when the filter or the data changes, never projected in
    /// the markup: the grid diffs its item collection by reference, so a freshly built list on
    /// every render would throw away whatever column the operator had sorted by.
    /// </para>
    /// </summary>
    private List<BoardRow> Visible => _visible;

    /// <summary>The books the desk polls (PolledBooks); empty when unknown.</summary>
    private IReadOnlyList<string> _polled = [];

    /// <summary>The main sportsbooks over the games in view (MainCoverage); a main book counts when it prices any of them.</summary>
    private MainCoverage _main = MainCoverage.Of([], null);

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task OnSportChanged(string value)
    {
        _sport = value ?? string.Empty;
        await LoadAsync();
    }

    private async Task SetWindowAsync(int hours)
    {
        _windowHours = hours;
        await LoadAsync();
    }

    /// <summary>Narrowing the view costs nothing — the rows are already loaded.</summary>
    private void SetPhase(BoardPhase phase)
    {
        _phase = phase;

        // A row left open under another filter would linger as a detached strip.
        _open = null;
        _snippet = null;
        _take = null;
        ApplyFilter();
    }

    /// <summary>
    /// The load in flight, cancelled by the next, so a slower read of the last league or horizon
    /// cannot land last and paint it under the new filter — and no superseded read runs to its
    /// end for nothing. Its <see cref="LatestRead.Gone"/> is for the alert recount, which is not a load.
    /// </summary>
    private readonly LatestRead _reads = new();

    /// <summary>
    /// The board's reads, together: the rows, today's moves, the alert counts and the league
    /// names each have a context of their own and none needs another's answer, so the wait is
    /// the slowest read's rather than the sum. Everything is read into locals and handed over at
    /// once, and only by the latest load: a newer filter cancels this one (<see cref="LatestRead.Next"/>),
    /// and one that comes back anyway hands over nothing. The token is taken inside the work so
    /// Try again, which repeats it, starts a read of its own.
    /// </summary>
    private async Task LoadAsync()
    {
        await WithActivityAsync(async () =>
        {
            var ct = _reads.Next();
            var sport = _sport;

            await LatestRead.HandOverAsync(ct,
                () => Data.ReadAsync(
                    string.IsNullOrEmpty(sport) ? null : sport,
                    TimeSpan.FromHours(_windowHours),
                    Lookback,
                    ct),
                read => HandOver([.. read.Rows], read.Port, read.Moves, (read.Alerts.Open, read.Alerts.Critical),
                    read.Names, read.Polled, read.PaidOnly));
        },
        settle: () => _rows.Count == 0 ? PulseState.Idle : PulseState.Healthy,
        status: () => _rows.Count == 0 ? "no games" : $"{_priced}/{_rows.Count} priced");
    }

    /// <summary>What the latest load read, onto the board at once.</summary>
    private void HandOver(
        List<BoardRow> rows, StatsPort? port, long moves, (int Open, int Critical) alerts, IReadOnlyDictionary<string, string> names,
        IReadOnlyList<string>? polled = null, IReadOnlyList<string>? paidOnly = null)
    {
        _rows = rows;
        _polled = polled ?? [];
        _main = MainCoverage.Of(_rows.SelectMany(BookCoverage.Books), _polled, paidOnly);
        _bookCount = _rows.Count == 0 ? 0 : _rows.Max(r => r.BookCount);
        _fewestBooks = _rows.Where(r => r.BookCount > 0).Select(r => r.BookCount).DefaultIfEmpty().Min();
        _priced = _rows.Count(r => r.HasPrices);
        _unpriced = _rows.Count - _priced;
        _port = port;

        // Age is only reported for the live market. BoardRow already withholds it from a
        // closing row, so this reads "how long since anything moved" rather than "how old
        // is the oldest number on screen".
        _oldest = _rows.Select(r => r.Age).Where(a => a is not null).DefaultIfEmpty().Max();

        _movesToday = moves;
        (_alerts, _criticalAlerts) = alerts;

        BuildSportOptions(names);
        BuildPhaseOptions();
        ApplyFilter();
    }

    /// <summary>
    /// The alert tile after a notice of alerts alone (<see cref="RefreshAsync(IReadOnlySet{string})"/>).
    /// Outside <c>WithActivityAsync</c> on purpose: it neither cancels a load in flight nor
    /// becomes the load Try again repeats, and a failure is the base class's "refresh failed".
    /// </summary>
    private async Task RecountAlertsAsync()
    {
        (_alerts, _criticalAlerts) = await Data.AlertCountsAsync(_reads.Gone);
    }

    private void ApplyFilter()
    {
        _visible = _phase switch
        {
            BoardPhase.Upcoming => _rows.Where(r => r.Game.Status == GameStatus.Scheduled).ToList(),
            BoardPhase.Live => _rows.Where(r => r.Game.Status == GameStatus.Live).ToList(),
            BoardPhase.Played => _rows
                .Where(r => r.Game.Status is GameStatus.Final or GameStatus.Postponed)
                .ToList(),
            _ => _rows
        };

        if (!string.IsNullOrWhiteSpace(_team))
        {
            _visible = _visible.Where(r => SearchService.MatchesAllWords(_team,
                r.Game.HomeTeam?.Name, r.Game.AwayTeam?.Name,
                r.Game.HomeTeam?.Abbrev, r.Game.AwayTeam?.Abbrev)).ToList();
        }

        // Counted over the whole slate, so the key says how many there are before it is pressed;
        // and let go of once a refresh leaves none, rather than showing an empty board for a
        // reason the operator can no longer see.
        _valueRows = _rows.Count(r => r.BestEv > 0);
        if (_valueRows == 0)
            _valueOnly = false;

        if (_valueOnly)
            _visible = _visible.Where(r => r.BestEv > 0).OrderByDescending(r => r.BestEv).ToList();

        _closing = _visible.Count(r => r.PricesAreClosing);

        // A filter or a reload that drops the open row closes it, as a phase change does. Left
        // open, the row would come back with a fresh strip showing nothing chosen while the
        // snippet it no longer owns still hung under it.
        _open = StillOpen(_open, _visible);
        if (_open is null)
        {
            _snippet = null;
            _take = null;
        }
    }

    /// <summary>
    /// The league filter's options: every requested league, plus any league that turned up on
    /// the board without being asked for.
    ///
    /// <para>
    /// Built from configuration rather than only from the data, so a league the desk is polling
    /// still appears on a day it has no fixtures. The operator asked for it, and an option that
    /// vanishes when the answer is "none" cannot be told apart from one that was never offered
    /// — ADR 0003's two kinds of zero. The count rides in the label, so an empty league says so
    /// before it is chosen.
    /// </para>
    /// </summary>
    private void BuildSportOptions(IReadOnlyDictionary<string, string> names)
    {

        // Counts only mean anything on an unfiltered load; once a league is selected the board
        // holds that league alone, so the other options keep whatever they last showed.
        if (_sport.Length == 0)
        {
            _counts = _rows
                .GroupBy(r => r.Game.Sport?.Key ?? string.Empty)
                .Where(g => g.Key.Length > 0)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }

        var keys = IngestionOptions.Value.EffectiveSports
            .Concat(_counts.Keys)
            .Concat(names.Keys)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _sportOptions = keys.Select(k =>
        {
            var name = names.TryGetValue(k, out var n) ? n : k.ToUpperInvariant();
            var count = _counts.TryGetValue(k, out var c) ? c : 0;

            return new SportOption(k, count == 0 ? name : $"{name} · {count}");
        }).ToList();
    }

    private Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The phase gate's positions, offered only for the phases actually on the board.
    ///
    /// A board of nothing but scheduled games has no "in play" to switch to, and a gate holding
    /// positions that cannot change what is on screen teaches the operator to ignore it.
    /// </summary>
    private void BuildPhaseOptions()
    {
        var live = _rows.Count(r => r.Game.Status == GameStatus.Live);
        var played = _rows.Count(r => r.Game.Status is GameStatus.Final or GameStatus.Postponed);
        var upcoming = _rows.Count(r => r.Game.Status == GameStatus.Scheduled);

        var options = new List<DeskSwitchOption<BoardPhase>>
        {
            new(BoardPhase.Any, "All", $"Every game in the window — {_rows.Count}")
        };

        if (upcoming > 0 && (live > 0 || played > 0))
            options.Add(new(BoardPhase.Upcoming, "Next", $"Not started yet — {upcoming}"));

        if (live > 0)
            options.Add(new(BoardPhase.Live, "Live", $"Being played now — {live}"));

        if (played > 0)
            options.Add(new(BoardPhase.Played, "Final", $"Finished or postponed — {played}"));

        _phaseOptions = options.Count > 1 ? options : [];

        // A filter left pointing at a phase that has emptied would show nothing for a reason
        // the operator did not choose.
        if (!_phaseOptions.Any(o => EqualityComparer<BoardPhase>.Default.Equals(o.Value, _phase)))
            _phase = BoardPhase.Any;
    }

    /// <summary>Whether Line watch is the workspace on the desk, and a press here drives Line movement.</summary>
    private bool Watching => LineWatch.IsActive(Manager);

    /// <summary>Shows the game, and from a price its market and side, in the Line movement window.</summary>
    private void Watch(BoardRow row, string? market, string? outcome)
        => LineWatch.Show(Manager, row.Game.Id, market, outcome, Matchup(row.Game), WindowId);

    // The hint and the press rule follow the workspace: applied or left while the Board is open,
    // the Board redraws when it changes.
    private bool _watchingDrawn;

    private void OnDeskChanged()
    {
        if (Watching != _watchingDrawn)
            _ = InvokeAsync(StateHasChanged);
    }

    protected override void OnAfterRender(bool firstRender) => _watchingDrawn = Watching;

    protected override void OnInitialized()
    {
        Slip.Changed += OnSlipChanged;
        Manager.Changed += OnDeskChanged;
    }

    /// <summary>The stats source the registry reported at the last load; configuration's answer before one.</summary>
    private StatsPort? _port;

    /// <summary>What fills the board: ESPN, The Odds API on a key, or nothing yet (ADR 0026).</summary>
    internal StatsPort Port => _port ?? (IngestionOptions.Value.Espn.Enabled ? StatsPort.Espn : StatsPort.None);

    protected override void OnDispose()
    {
        Slip.Changed -= OnSlipChanged;
        Manager.Changed -= OnDeskChanged;

        // A closed window's reads have nobody to hand over to.
        _reads.Cancel();
    }

    /// <summary>A leg banked or removed anywhere redraws the cells' "on slip" marks.</summary>
    private void OnSlipChanged() => _ = InvokeAsync(StateHasChanged);
}
