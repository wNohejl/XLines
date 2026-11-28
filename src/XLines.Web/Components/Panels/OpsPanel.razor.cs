using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.Changes;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Reliability;
using XLines.Web.Components.Desk;
using XLines.Web.Components.Snippets;
using XLines.Web.Services;
using XLines.Web.Windowing;

namespace XLines.Web.Components.Panels;

/// <summary>
/// The Ops window's state, its load and the operator's writes: the key, the scanning mode, the
/// drill, the evaluation and the promotion of an alert. Each write handler is the operator's alone
/// and says so first (OperatorWriteGuardTests). The markup is <c>OpsPanel.razor</c>; the prose that
/// prices scanning is <c>OpsScanningCost.razor</c>.
/// </summary>
public partial class OpsPanel : PanelBase
{
    [Inject] private IOpsData Data { get; set; } = default!;
    [Inject] private IOptions<ReliabilityOptions> Options { get; set; } = default!;
    [Inject] private IOptions<XLines.Ingestion.Configuration.IngestionOptions> Ingestion { get; set; } = default!;
    [Inject] private DeskToasts Toasts { get; set; } = default!;

    protected override IReadOnlySet<string> Watches { get; } = new HashSet<string> { DataTopics.Runs, DataTopics.Alerts };

    protected override Task RefreshAsync() => LoadAsync();

    private IReadOnlyList<SourceHealth> _health = [];
    private IReadOnlyList<OddsProviderState> _feeds = [];
    private PollPlan? _plan;

    private XLines.Ingestion.Configuration.LinePollingOptions LinePolling => Ingestion.Value.LinePolling;

    /// <summary>Whether lines are scanned unasked — the stored choice where there is one, else configuration.</summary>
    private bool _unattended;
    private bool _pollingSetHere;
    private bool _confirmAutomatic;

    // Every action below catches its own failure. PanelBase catches a load, not a write, and an
    // exception escaping an event handler ends the circuit — the whole desk, not this window.
    // The operator is told in one sentence what did not happen; the window keeps what it shows.

    private async Task SetPollingAsync(bool automatic)
    {
        if (!Manager.Access.CanOperate)
            return;

        try
        {
            await Data.SetLinePollingAsync(automatic);

            _confirmAutomatic = false;
            Toasts.Success(automatic
                ? "Lines are scanned automatically, paced to the allowance."
                : "Lines are pulled on request only. Nothing more is spent unasked.");

            await LoadAsync();
        }
        catch (Exception ex)
        {
            // The confirm row stays open on a failed yes, so trying again is the key already
            // under the pointer.
            Log.LogError(ex, "Line polling was not set to {Mode}", automatic ? "automatic" : "manual");
            Toasts.Fail("Scanning was not changed. Try again.");
        }
    }

    // The Odds API key, entered here. Null on a host that takes its key from configuration only.
    private XLines.Web.Services.DeskOddsKey? _oddsKey;
    private string? _keyHint;
    private string? _keyDraft;
    private bool _replacingKey;
    private bool _keyBusy;

    /// <summary>The Remove question is open in the key's row.</summary>
    private bool _confirmRemove;

    private string NoFeedText => _oddsKey is not null && Manager.Access.CanOperate
        ? "No odds provider is configured, so nothing prices the board. A key for The Odds API, entered below, adds one."
        : "No odds provider is configured, so nothing prices the board. A provider's key in the web host's configuration adds one.";

    private void CancelReplace()
    {
        _replacingKey = false;
        _keyDraft = null;
    }

    private async Task SaveKeyAsync()
    {
        if (!Manager.Access.CanOperate)
            return;

        if (_oddsKey is null || string.IsNullOrWhiteSpace(_keyDraft) || _keyBusy)
            return;

        _keyBusy = true;
        try
        {
            var check = await _oddsKey.SaveAsync(_keyDraft);

            switch (check.Verdict)
            {
                case XLines.Web.Services.OddsKeyVerdict.Accepted:
                    _keyDraft = null;
                    _replacingKey = false;
                    Toasts.Success(check.CreditsRemaining is { } left
                        ? $"Key accepted and saved. {left:N0} credits left this month."
                        : "Key accepted and saved.");
                    await LoadAsync();
                    break;

                case XLines.Web.Services.OddsKeyVerdict.Rejected:
                    Toasts.Fail("The Odds API refused that key. Check it against the email from the-odds-api.com; an odds-api.io key is a different provider's.");
                    break;

                default:
                    Toasts.Fail("The Odds API could not be reached, so the key was not saved. Try again.");
                    break;
            }
        }
        catch (Exception ex)
        {
            // The type only: nothing that could carry the key reaches the log.
            Log.LogError("The odds key was not saved ({Kind})", ex.GetType().Name);
            Toasts.Fail("The key was not saved. Try again.");
        }
        finally
        {
            _keyBusy = false;
        }
    }

    private async Task RemoveKeyAsync()
    {
        if (!Manager.Access.CanOperate)
            return;

        if (_oddsKey is null || _keyBusy)
            return;

        _keyBusy = true;
        try
        {
            await _oddsKey.RemoveAsync();
            _confirmRemove = false;
            Toasts.Success("Key removed from this desk.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The odds key was not removed");
            Toasts.Fail("The key was not removed. Try again.");
        }
        finally
        {
            _keyBusy = false;
        }
    }

    /// <summary>The two ways lines are fetched, as the Scanning row's segments.</summary>
    private static readonly IReadOnlyList<DeskSwitchOption<bool>> ScanModes =
    [
        new(false, "On request", "Nothing is spent unasked: lines and scores are pulled only from Pull data"),
        new(true, "Automatic", "Lines are scanned on a schedule paced to the month's credits; scores, where The Odds API supplies them, once a night per league")
    ];

    // ---- The stats source and the month (ADR 0026) ---------------------------------------------

    /// <summary>What supplies the schedule here. Configuration's answer until the registry has been read.</summary>
    private StatsPort _port;

    /// <summary>The Odds API account's month, where its feed or its scores are in use here.</summary>
    private AllowanceState? _allowance;

    /// <summary>What scores would take from the month, where they are metered here.</summary>
    private ScoresPlan? _scores;

    private int ScoresCredits => _scores?.CreditsPerCall ?? XLines.Ingestion.Adapters.TheOddsApiScheduleAdapter.CreditsWithDaysFrom;

    protected override void OnInitialized()
    {
        _port = Ingestion.Value.Espn.Enabled ? StatsPort.Espn : StatsPort.None;
        base.OnInitialized();
    }

    private const string StatsSourceTip =
        "The stats source writes each game's schedule and result, which the board lists and settlement grades. "
        + "One runs at a time: ESPN when you turn it on, otherwise The Odds API on your key.";

    private const string KeyTip =
        "Your key for the-odds-api.com. Free keys carry 500 credits a month, reset on the 1st. "
        + "Lines cost one credit per market per league; scores two a league; fixtures nothing.";

    private const string ScanningTip =
        "On request spends nothing unless you choose a pull from Pull data. Automatic scans lines on a schedule "
        + "paced so the month's credits last, keeping a reserve for pulls on request, and pulls scores once a night "
        + "per league where The Odds API is the stats source. Credits reset on the 1st.";

    /// <summary>
    /// The segment pressed. Automatic is held on the confirm row until it is said yes to, so the
    /// control shows the choice while the question beside it is open; On request stops spending
    /// at once, and pressing the mode already in force withdraws a pending question.
    /// </summary>
    private async Task ChooseScanningAsync(bool automatic)
    {
        if (automatic == _unattended)
        {
            _confirmAutomatic = false;
            return;
        }

        if (automatic)
        {
            _confirmAutomatic = true;
            return;
        }

        await SetPollingAsync(automatic: false);
    }

    /// <summary>The switch is the operator's, and only offered when there is a live feed to scan.</summary>
    private bool ShowsScanning => Manager.Access.CanOperate && _feeds.Any(f => f.IsLive);

    /// <summary>The ids the hand-spelled cards' bars label them by; one window's, never another's.</summary>
    private readonly string _uid = "ops-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The stats source in a word, for the Stats source card's bar.</summary>
    private string StatsSourceName => _port switch
    {
        StatsPort.Espn => "ESPN",
        StatsPort.TheOddsApi => "The Odds API",
        _ => "None"
    };

    /// <summary>The league a drill with no fault in would pull, while its price is being asked about.</summary>
    private string? _drillAsk;

    private bool _drillBusy;

    /// <summary>"Pull MLB lines now, about 2 credits of 412 left? …" — the price before the press.</summary>
    private string DrillQuestion(string sport)
    {
        var credits = _marketsBySport
            .FirstOrDefault(m => string.Equals(m.Sport, sport, StringComparison.OrdinalIgnoreCase))?.Credits;
        var price = credits is > 0 ? $", about {credits:N0} {(credits == 1 ? "credit" : "credits")}" : "";
        var left = _allowance is { } month ? $" of {month.Remaining:N0} left this month" : "";

        return $"No fault is injected, so the drill pulls {sport.ToUpperInvariant()} lines for real{price}{left}. Run it?";
    }

    /// <summary>
    /// The drill's key. A fault injected means nothing is bought, so it runs at once; otherwise the
    /// league it would pull is found first and the row asks before spending on it.
    /// </summary>
    private async Task AskDrillAsync()
    {
        if (_injectable.Any(s => s.FailureMode is not null))
        {
            await DrillAsync();
            return;
        }

        try
        {
            var sport = await Data.DrillSportAsync();

            if (sport is null)
            {
                Toasts.Warn("Drill skipped — no league is configured to scan.");
                return;
            }

            _drillAsk = sport;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The drill's league was not found");
            Toasts.Fail("The drill did not run. Try again.");
        }
    }

    /// <summary>
    /// Where the mode in force came from — said only beside the switch that sets it. It follows
    /// a sentence, and Razor drops the whitespace before an expression, so the space is its own.
    /// </summary>
    private string? Provenance => !ShowsScanning
        ? null
        : _pollingSetHere
            ? " Chosen here, for this host and the worker alike."
            : " Set by configuration until it is chosen here.";

    private IReadOnlyList<SportMarkets> _marketsBySport = [];

    /// <summary>A provider by the name the feeds table gives it; the planner reports its key.</summary>
    private string FeedName(string key)
        => _feeds.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))?.Name ?? key;

    /// <summary>
    /// A rule by name rather than by key: "volume_anomaly" reads as "Volume anomaly". The key
    /// stays on the cell's tooltip, because it is what the runbook files the rule under.
    /// </summary>
    private static string RuleName(string key)
        => string.IsNullOrEmpty(key) ? key : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');

    private IReadOnlyList<Alert> _alerts = [];
    private List<Source> _injectable = [];
    private int _healthy;
    private int _criticals;
    private int _openIncidents;
    private long _rowsToday;
    private bool _busy;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    // Pulled out of the markup when the table became a grid: a cell template evaluates per
    // render, and both of these were previously computed once per row in a foreach.
    private bool IsStale(SourceHealth h)
        => !h.NeverRun && h.IsStale(Options.Value.FreshnessSlo);

    private bool IsRateLow(SourceHealth h)
        => h.RunsInWindow >= 3 && h.SuccessRate < Options.Value.SuccessRateSlo;

    protected override string LoadSubject => "Source health";

    // This window is the reason the pulse exists: it reports the platform's worst state,
    // so a minimised Ops window still shows red when something breaches.
    private Task LoadAsync() => WithActivityAsync(ReadAsync,
        () => _criticals > 0 ? PulseState.Critical
            : _alerts.Count > 0 ? PulseState.Warn
            : PulseState.Healthy,
        () => _alerts.Count == 0 ? "all clear" : $"{_alerts.Count} open");

    /// <summary>
    /// The load in flight, cancelled by the next. Every runs and alerts notice reloads this window,
    /// and a read of ten queries overtaken by a newer one must neither run on nor land last.
    /// </summary>
    private readonly LatestRead _reads = new();

    protected override void OnDispose() => _reads.Cancel();

    /// <summary>
    /// Everything read into locals and handed over at once, and only by the latest load. The
    /// reads share the scope's one context, so they stay one after another. The token is taken
    /// here, inside the work, so Try again, which repeats it, starts a read of its own.
    /// </summary>
    private async Task ReadAsync()
    {
        var ct = _reads.Next();

        await LatestRead.HandOverAsync(ct, () => FetchAsync(ct), HandOver);
    }

    private Task<OpsRead> FetchAsync(CancellationToken ct) => Data.ReadAsync(ct);

    private void HandOver(OpsRead read)
    {
        _feeds = read.Feeds;
        _oddsKey = read.OddsKey;
        _keyHint = _oddsKey?.Hint;
        _pollingSetHere = read.PollingSetHere;
        _unattended = read.Unattended;
        _plan = read.Plan;
        _marketsBySport = read.MarketsBySport;
        _port = read.Port;
        _scores = read.Scores;
        _allowance = read.Allowance;
        _health = read.Health;
        _alerts = read.Alerts;

        // In SLO means both SLOs: a source the table tags Degraded (success rate under its floor)
        // is not counted as in it, so the tile and the table agree.
        var o = Options.Value;
        _healthy = _health.Count(h => h.NeverRun || (!h.IsStale(o.FreshnessSlo) && !IsRateLow(h)));
        _criticals = _alerts.Count(a => a.Severity == AlertSeverity.Critical);
        _openIncidents = read.OpenIncidents;
        _rowsToday = read.RowsToday;
        _injectable = [.. read.Injectable];
    }

    // Identity, not arithmetic. The same EvaluateAsync pass that opens an alert also
    // auto-resolves any whose condition has cleared, so a count delta reports zero — or a
    // negative — for a pass that genuinely raised something. On a drill that is the exact
    // answer being asked for, so the two are compared by id.
    private HashSet<long> OpenAlertIds() => _alerts.Select(a => a.Id).ToHashSet();

    private int CountNew(HashSet<long> seen) => _alerts.Count(a => !seen.Contains(a.Id));

    private static DeskState SeverityState(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => DeskState.Negative,
        AlertSeverity.Warn => DeskState.Warning,
        _ => DeskState.Info
    };

    private async Task EvaluateAsync()
    {
        if (!Manager.Access.CanOperate)
            return;

        _busy = true;
        Report(PulseState.Active, "evaluating");

        try
        {
            await Data.EvaluateAsync();

            var seen = OpenAlertIds();
            await LoadAsync();

            // A pass that changes nothing repaints identically to one that did not run, and
            // the rollup takes long enough that the operator has usually looked away. The
            // notice reports the outcome, not the click — and it is transient because the
            // alert table below is the durable record either way.
            var opened = CountNew(seen);

            if (_criticals > 0)
                Toasts.Warn($"Evaluated — {_alerts.Count} open, {_criticals} critical.");
            else if (opened > 0)
                Toasts.Warn($"Evaluated — {opened} new alert{(opened == 1 ? "" : "s")} opened.");
            else
                Toasts.Success($"Evaluated — {(_alerts.Count == 0 ? "every source inside its SLO" : $"{_alerts.Count} open, none critical")}.");
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The evaluation did not run");
            Toasts.Fail("The evaluation did not run. Try again.");

            // The pulse still says "evaluating". A read settles it on what the platform holds,
            // and catches its own failure if the database is what went.
            await LoadAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SetModeAsync(int sourceId, string? mode)
    {
        if (!Manager.Access.CanOperate)
            return;

        try
        {
            await Data.SetFailureModeAsync(sourceId, mode);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The fault on source {Source} was not set", sourceId);
            Toasts.Fail("The fault was not set. Try again.");
        }
    }

    private async Task DrillAsync()
    {
        if (!Manager.Access.CanOperate)
            return;

        // Its own flag: sharing Evaluate's set both keys shimmering whichever one ran.
        _drillBusy = true;
        Report(PulseState.Active, "drill running");

        // The league the confirm priced is the league pulled.
        var asked = _drillAsk;

        try
        {
            var sport = await Data.DrillAsync(asked);

            if (sport is null)
            {
                Toasts.Warn("Drill skipped — no league is configured to scan.");
                return;
            }

            var seen = OpenAlertIds();
            await LoadAsync();

            // The drill's whole point is "did the fault get caught?", and a drill that caught
            // nothing looks exactly like one that has not finished. Two outcomes, one line.
            var fired = CountNew(seen);

            // Naming the league is not decoration: with no fault injected the drill performs a
            // real, metered pull, and an operator who cannot see what it bought cannot tell a
            // drill from a spend.
            var league = sport.ToUpperInvariant();

            if (fired > 0)
                Toasts.Success($"Drill complete on {league} — {fired} alert{(fired == 1 ? "" : "s")} fired.");
            else
                Toasts.Warn($"Drill complete on {league} — no new alerts fired.");
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The drill did not run");
            Toasts.Fail("The drill did not run. Try again.");

            // A drill can fail part-way, after an ingest has landed or an alert has opened; the
            // read shows what did happen and settles the pulse it left on "drill running".
            await LoadAsync();
        }
        finally
        {
            _drillBusy = false;
            _drillAsk = null;
        }
    }

    private async Task PromoteAsync(long alertId)
    {
        if (!Manager.Access.CanOperate)
            return;

        try
        {
            await Data.PromoteAsync(alertId);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // No incidents window either: raising it would show a list without the incident the
            // operator just asked for.
            Log.LogError(ex, "Alert {Alert} was not promoted", alertId);
            Toasts.Fail("The alert was not promoted. Try again.");
            return;
        }

        // Raise the incidents window so the promotion has somewhere visible to land.
        OpenIncidents();
    }

    private void OpenIncidents()
    {
        if (WindowCatalog.Find(WindowCatalog.Incidents) is { } definition)
            Manager.Open(definition);
    }
}
