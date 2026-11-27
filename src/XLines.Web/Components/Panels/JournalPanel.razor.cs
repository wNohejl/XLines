using Microsoft.AspNetCore.Components;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.Changes;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Ingestion.Services;
using XLines.Web.Components.Desk;
using XLines.Web.Services;
using XLines.Web.Windowing;
using static XLines.Web.Components.Panels.JournalDisplay;

namespace XLines.Web.Components.Panels;

/// <summary>
/// The Journal: straight bets and parlays already placed elsewhere, their filters, settlement
/// and the slip as a tab. This part holds the window's state, its lifecycle and its one read;
/// the edit forms are in JournalPanel.Editing.cs and the row and toolbar keys in
/// JournalPanel.Actions.cs.
/// </summary>
public partial class JournalPanel
{
    [Inject] private IJournalData Data { get; set; } = default!;

    [Inject] private IDeskAlerts Alerts { get; set; } = default!;

    [Inject] private IWagerData Wagers { get; set; } = default!;

    [Inject] private WagerSlip Slip { get; set; } = default!;

    [Inject] private DeskToasts Toasts { get; set; } = default!;

    private enum JournalTab { Journal, Slip }

    private JournalTab _tab;

    // An empty slip cannot be opened: a tab that opens onto "nothing on the slip" is a click
    // spent to learn so, and the Journal's Place wager key is the way to a wager. The tab keeps
    // its place in the bar, disabled, so the bar does not change shape as bets come and go;
    // it stays live while the operator is on it, so a slip just emptied by logging is not
    // pulled from under the report of what it logged.
    // The first tab reads "Entries", not "Journal": the window's own tab in the title-bar strip
    // already says Journal, and the two selected one above the other read as one word twice. The
    // key stays JournalTab.Journal, so nothing that opens a tab by its key changes.
    private IReadOnlyList<DeskTab<JournalTab>> Tabs =>
    [
        new(JournalTab.Journal, "Entries"),
        new(JournalTab.Slip, "Slip", Count: Slip.Items.Count, Enabled: Slip.Items.Count > 0 || _tab == JournalTab.Slip)
    ];

    protected override IReadOnlySet<string> Watches { get; } = new HashSet<string> { DataTopics.Journal, DataTopics.Games };

    // Games are watched for the entries' scores and finals, so only the games an entry or a leg
    // on screen is on: a live poll of some other league used to re-read the whole journal. Every
    // journal notice and a bare games notice still reload; a new entry arrives as a journal one.
    protected override IReadOnlyDictionary<string, IReadOnlySet<string>> WatchesIds => _held;

    private IReadOnlyDictionary<string, IReadOnlySet<string>> _held = HeldGames([], []);

    /// <summary>
    /// The ids <see cref="WatchesIds"/> narrows the games topic to: every game an entry or a
    /// parlay's leg is on. Built once per load and swapped whole, because the base reads it on the
    /// publisher's thread.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> HeldGames(
        IEnumerable<JournalEntry> entries, IEnumerable<Parlay> parlays)
        => NoticeTokens.Narrow(
            entries.Concat(parlays.SelectMany(p => p.Legs)).Select(e => e.GameId).OfType<int>(), DataTopics.Games);

    protected override Task RefreshAsync() => LoadAsync();

    private const string FreeText = "__free__";
    private const int Step = 100;

    private enum FormMode { None, Entry, EditParlay }

    private static readonly IReadOnlyList<DeskSwitchOption<JournalStatus>> Statuses =
    [
        new(JournalStatus.All, "All"),
        new(JournalStatus.Pending, "Pending"),
        new(JournalStatus.Settled, "Settled")
    ];

    // 0 is "all time": the switch needs a value for it, and no journal is older than that.
    private static readonly IReadOnlyList<DeskSwitchOption<int>> Periods =
    [
        new(7, "7d", "Placed in the last 7 days"),
        new(30, "30d", "Placed in the last 30 days"),
        new(90, "90d", "Placed in the last 90 days"),
        new(0, "All", "Every entry")
    ];

    private List<JournalEntry> _entries = [];
    private List<Parlay> _parlays = [];
    private List<string> _books = [];
    private List<string> _sports = [];

    private JournalStatus _status = JournalStatus.All;
    private int _days;
    private string? _sport;
    private string? _bookFilter;
    private int _take = Step;

    private FormMode _mode;
    private Draft _entry = new();
    private JournalEntry? _editing;

    // The parlay being edited, and its changes as the form opened, to tell an edit from a look.
    private ParlayEditDraft? _parlay;
    private ParlayEdit? _parlayOpened;

    private bool _busy;
    private string? _message;

    private bool Filtered => _status != JournalStatus.All || _days != 0 || _sport is { Length: > 0 } || _bookFilter is { Length: > 0 };

    /// <summary>"All", then each enabled league; "" is all, as the filter reads it.</summary>
    private IReadOnlyList<DeskSwitchOption<string>> SportOptions =>
        [new("", "All", "Every sport"), .. _sports.Select(s => new DeskSwitchOption<string>(s, s.ToUpperInvariant()))];

    protected override async Task OnInitializedAsync()
    {
        // The tab's count follows the slip.
        Slip.Changed += OnSlipChanged;

        // In Line watch the Journal starts on the bets a moving line still matters for: the ones
        // not yet settled. A default, not a lock — the status switch shows everything again.
        if (XLines.Web.Windowing.LineWatch.IsActive(Manager))
            _status = JournalStatus.Pending;

        await LoadAsync();
    }

    protected override void OnDispose()
    {
        Slip.Changed -= OnSlipChanged;
        _reads.Cancel();
    }

    private void OnSlipChanged() => _ = InvokeAsync(StateHasChanged);

    protected override string LoadSubject => "The journal";

    private int _pending;

    private Task LoadAsync() => WithActivityAsync(ReadAsync,
        () => _pending > 0 ? PulseState.Warn : PulseState.Healthy,
        () => _entries.Count + _parlays.Count == 0 && !Filtered ? null : $"{_pending} pending");

    /// <summary>
    /// The load in flight, cancelled by the next. The base class's generation check kept a
    /// superseded load from saying how the window stood, but not from handing over its rows, so a
    /// slower read of the last filter could land last and paint it under the new one.
    /// </summary>
    private readonly LatestRead _reads = new();

    /// <summary>
    /// The token of the load in flight, for <see cref="FetchAsync"/> to pass to its reads. A
    /// property rather than a parameter so a render test's override keeps its signature; it is
    /// read before the fetch's first await, while it still names that fetch's load.
    /// </summary>
    protected CancellationToken ReadToken { get; private set; }

    /// <summary>
    /// Everything is read first and handed over at once, and only by the latest load: a newer
    /// filter or notice cancels this one, and one that comes back anyway hands over nothing. The
    /// token is taken inside the work so Try again, which repeats it, starts a read of its own.
    /// </summary>
    private async Task ReadAsync()
    {
        var ct = _reads.Next();
        ReadToken = ct;

        var filter = new JournalFilter(
            _status,
            _sport,
            _bookFilter,
            _days == 0 ? null : DateTimeOffset.UtcNow.AddDays(-_days),
            _take);

        await LatestRead.HandOverAsync(ct, () => FetchAsync(filter), read =>
        {
            _entries = read.Entries.ToList();
            _parlays = read.Parlays.ToList();
            _held = HeldGames(_entries, _parlays);
            _books = read.Books.ToList();
            _sports = read.Sports.ToList();
            _pending = read.Pending;
        });
    }

    /// <summary>
    /// The Journal's one read of the database. Virtual so a render test can hand the panel a
    /// journal: the test project has no database, and a row's keys are only drawn on a row
    /// that loaded.
    /// </summary>
    protected virtual Task<JournalRead> FetchAsync(JournalFilter filter) => Data.ReadAsync(filter, ReadToken);

    /// <summary>The write behind the parlay form's Save. Virtual for the same render tests.</summary>
    protected virtual Task WriteParlayAsync(Guid id, ParlayEdit edit) => Data.UpdateParlayAsync(id, edit);

    /// <summary>The write behind the entry form's Save. Virtual for the same render tests.</summary>
    protected virtual Task WriteEntryAsync(EntryDraft draft, int? id) => Data.SaveEntryAsync(draft, id);

    private async Task FilterAsync(Action change)
    {
        change();
        _take = Step;
        await LoadAsync();
    }

    private async Task ShowMoreAsync()
    {
        _take += Step;
        await LoadAsync();
    }
}
