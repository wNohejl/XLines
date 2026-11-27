using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data.Changes;
using XLines.Data.CrossReference;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Ingestion.Services;
using XLines.Web.Components.Desk;
using XLines.Web.Services;
using XLines.Web.Windowing;

namespace XLines.Web.Components.Panels;

/// <summary>
/// The wager window's doors, state and reads: what a door hands it, what it holds, and the row
/// and journal it reads. Choosing the bet is <c>BoardWager.Choice.cs</c>; banking and logging it
/// <c>BoardWager.Commit.cs</c>; the words it shows <c>BoardWager.Text.cs</c>; the markup
/// <c>BoardWager.razor</c>.
/// </summary>
public partial class BoardWager : PanelBase
{
    [Inject] private IWagerData Data { get; set; } = default!;
    [Inject] private WagerSlip Slip { get; set; } = default!;
    [Inject] private DeskToasts Toasts { get; set; } = default!;

    private enum WagerTab { Wager, Slip }

    private WagerTab _tab;

    // An empty slip cannot be opened: the tab keeps its place in the bar, disabled, until
    // something is banked, so the bar holds its shape. It stays live while the operator is on
    // it, so a slip emptied by logging is not pulled from under the report of what it logged.
    private IReadOnlyList<DeskTab<WagerTab>> Tabs =>
    [
        new(WagerTab.Wager, "Wager"),
        new(WagerTab.Slip, "Slip", Count: Slip.Items.Count, Enabled: Slip.Items.Count > 0 || _tab == WagerTab.Slip)
    ];

    /// <summary>
    /// The game this view is about. Taken as an id rather than a row so the view can be hosted
    /// as a desk window, which is rebuilt from its parameters and cannot carry an object. Absent:
    /// the Journal's door, which opens on a game search.
    /// </summary>
    [Parameter] public int? GameId { get; set; }

    /// <summary>The side a door names (<c>Markets.*</c>). With <see cref="Outcome"/>; otherwise the most value.</summary>
    [Parameter] public string? Market { get; set; }

    [Parameter] public string? Outcome { get; set; }

    /// <summary>The book a door names. Where it quotes the side, the window shows its price, not the best.</summary>
    [Parameter] public string? Book { get; set; }

    /// <summary>
    /// A bet being made again (<see cref="WagerPrefill"/>): the stake and note it was made with,
    /// so the copy is ready to log; the price only where the original was typed, which makes the
    /// copy a typed price too; the description of a bet on no game, which opens the no-game form
    /// filled in. The stake and the line are invariant text ("110", "-2.5"), because a window's
    /// parameters are saved as JSON and come back as an int or a double, neither of which sets a
    /// decimal (<see cref="WagerPrefill.ToParameters"/>).
    /// </summary>
    /// <remarks>
    /// Typed object, not string: a desk saved by the first build that carried a copy holds the
    /// stake as a number, which restores as an int or a double, and a window that cannot take
    /// what a saved desk hands it takes the whole circuit down. <see cref="WagerPrefill.Read"/>
    /// reads any of them.
    /// </remarks>
    [Parameter] public object? Stake { get; set; }

    [Parameter] public string? Note { get; set; }

    /// <summary>
    /// A typed price the copy types again, as invariant text ("104.35"): a price is taken to the
    /// cent, and a decimal parameter cannot be set from what a saved desk restores. A desk saved
    /// while the price was whole hands an int; <see cref="WagerPrefill.Read"/> reads either.
    /// </summary>
    [Parameter] public object? PriceAmerican { get; set; }

    [Parameter] public object? TakenLine { get; set; }

    /// <summary>
    /// Whatever else a saved desk hands this window — "Line" from the first build that carried a
    /// copy — captured rather than refused. Blazor throws on a parameter a component does not
    /// declare, and that throw ended the circuit, not just this window.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? Saved { get; set; }

    [Parameter] public string? Described { get; set; }

    /// <summary>
    /// The Journal's Build parlay door: every bet picked here is a leg on the slip rather than a
    /// bet of its own. No stake step, no Log; Add leg banks it and, on the game search, clears
    /// the game for the next one. The parlay is logged from the Slip tab.
    /// </summary>
    [Parameter] public bool BuildParlay { get; set; }

    [Parameter] public EventCallback OnPlaced { get; set; }

    protected override IReadOnlySet<string> Watches { get; } =
        new HashSet<string> { DataTopics.Odds, DataTopics.Journal };

    /// <summary>
    /// Odds or journal: the refresh does not say which, and both reads are cheap, so the row is
    /// re-priced and the journal re-read either way.
    /// </summary>
    protected override Task RefreshAsync() => ReloadAsync();

    private const int BetStep = 1, StakeStep = 2, ReviewStep = 3;

    /// <summary>The price row's "choose a side" text, which the disabled book pop-up is described by.</summary>
    private readonly string _waitId = "wait-" + Guid.NewGuid().ToString("N")[..8];

    private BoardRow? Row;
    private int? _gameId;
    private bool _noGame;
    private string? _described;

    // The side, and the book the operator chose for it. The book outlives a change of side: a bet
    // at DraftKings is usually followed by another at DraftKings.
    private string? _market;
    private string? _outcome;
    private string? _book;

    private WagerOffer? _offer;
    private IReadOnlyList<string> _knownBooks = [];

    private bool _manual;

    /// <summary>The typed price's fields and their one reading, shared with the Board take.</summary>
    private readonly TypedPrice _typed = new();

    // A finished step the operator opened again with Change; null while the steps run in order.
    private int? _opened;

    private bool _loading;
    private bool _settingsRead;
    private decimal _stake;
    private string? _note;
    private string? _error;
    private string? _logged;

    // What was last banked from this window, and the card it became: the review's "On the slip"
    // tag stands while the bet on screen is still exactly that one and the card is still there.
    private Banking? _banked;

    private sealed record Banking(Guid Card, BetShape Bet);

    /// <summary>What makes two bets the same bet for the slip's tag: the side, the price and where, and the stake.</summary>
    private sealed record BetShape(string Market, string Outcome, decimal? Line, string Book, decimal PriceAmerican,
                                   bool Manual, decimal Stake);

    private BetShape? ShapeNow
        => _offer is { } o ? new BetShape(o.Market, o.Outcome, o.Line, o.Book, o.PriceAmerican, o.Manual, _stake) : null;

    private bool OnSlip
        => _banked is { } banked && banked.Bet == ShapeNow && Slip.Items.Any(i => i.Id == banked.Card);

    // The bet just logged, said in one line above the steps until the next bet is begun.
    private LoggedWager? _receipt;

    private sealed record LoggedWager(string Bet, decimal PriceAmerican, string Book, decimal Stake, bool Manual,
                                      BetChoice Choice);

    /// <summary>
    /// What the operator chose for a bet, short of its stake: what Same bet again puts back after
    /// the wizard has reset itself for the next one.
    /// </summary>
    private sealed record BetChoice(string? Market, string? Outcome, string? Book, bool Manual, decimal? TypedPrice,
                                    decimal? TypedLine, string? TypedBook, string? Described, bool SideChosen);

    // What the door named, last time it was applied. A re-render that hands the same door back
    // leaves the window as the operator left it, so New bet is not undone by the window landing
    // on the door's side again.
    private string? _door;
    private string? _moved;
    private bool _saving;

    // The duplicate check's reading: a field the render reads, never a query from the render.
    private JournalEntry? _prior;
    private bool _priorRead;
    private bool _priorFailed;

    private IngestionJob? _linesJob;
    private bool _pulling;

    protected override void OnInitialized() => Slip.Changed += OnSlipChanged;

    protected override void OnDispose() => Slip.Changed -= OnSlipChanged;

    private void OnSlipChanged() => _ = InvokeAsync(StateHasChanged);

    protected override async Task OnParametersSetAsync()
    {
        var door = string.Join('|', GameId, Market, Outcome, Book, Stake, Note, PriceAmerican, TakenLine, Described, BuildParlay);

        if (door == _door)
            return;

        _door = door;

        // A new door is a new bet: whatever was logged through the last one has been seen.
        _receipt = null;

        // A first read shows the skeleton; a door re-aiming a loaded window does not flash it.
        _loading = Row is null && GameId is not null;
        _book = Book;
        _moved = null;
        _opened = null;

        try
        {
            if (!_settingsRead)
            {
                _knownBooks = await Data.KnownBooksAsync();
                _settingsRead = true;
            }

            if (GameId is { } id)
            {
                _gameId = id;
                await ReadRowAsync();
            }
        }
        finally
        {
            _loading = false;
        }

        // What a copy carries, applied before the price is decided so a typed price is typed
        // from the first render. A door that names none leaves the window as it was.
        if (WagerPrefill.Read(Stake) is { } stake && stake > 0)
            _stake = stake;
        if (Note is not null)
            _note = Note;

        if (Described is not null && GameId is null)
        {
            // The no-game form, filled in: the description, the price and the book it was at.
            _noGame = true;
            _described = Described;
            _typed.Restore(WagerPrefill.Read(PriceAmerican), _typed.Line, Book);
            Reprice();
            return;
        }

        if (GameId is null)
            return;

        // A door lands on the side it names, and only then; one that names none opens on step 1
        // with every side offered and none chosen. The book is the door's, else the best.
        var landed = SelectionChoice.Land(Row, Market, Outcome, null);
        _market = landed?.Market;
        _outcome = landed?.Outcome;
        var typedPrice = WagerPrefill.Read(PriceAmerican);
        _manual = typedPrice is not null;

        if (_manual)
            _typed.Restore(typedPrice, WagerPrefill.Read(TakenLine ?? Saved?.GetValueOrDefault("Line")), Book);

        Reprice();
        _fromClose = false;
        _keptClose = false;
        TypeOverClose();
        await ReadPriorAsync();
    }

    private async Task ReadRowAsync()
    {
        Row = _gameId is { } id ? await Data.RowAsync(id) : null;
        _linesJob = Row?.Game.Sport is { } sport ? await Data.LinesJobAsync(sport.Key) : null;
    }

    /// <summary>After a change the window watches: the same side and book, re-priced, and the journal re-read.</summary>
    private async Task ReloadAsync()
    {
        if (_gameId is null)
            return;

        var before = _offer;
        await ReadRowAsync();
        Reprice();

        if (before is { Manual: false } && _offer is { Manual: false }
            && _offer.Market == before.Market && _offer.Outcome == before.Outcome
            && string.Equals(_offer.Book, before.Book, StringComparison.OrdinalIgnoreCase)
            && (_offer.PriceAmerican != before.PriceAmerican || _offer.Line != before.Line))
        {
            _moved = $"Price moved since you opened this: {LinePrice(before)} → {LinePrice(_offer)} ({_offer.BookName}).";
            // A number that moved is worth a look: the step opens to show it.
            _opened = BetStep;
        }

        await ReadPriorAsync();
    }

    /// <summary>
    /// The duplicate check's one query, keyed on the side (game, market, outcome) at any book. It
    /// reports the title bar's pulse itself — active while it runs, healthy or a warning after —
    /// and stays outside WithActivityAsync on purpose: that wrapper treats a failure as the
    /// window not loading — LoadError set, the pulse critical with "load failed" — and keeps the
    /// work it ran as the one Retry repeats. A journal that cannot be read is not the window
    /// failing; it leaves one check unable to answer, so the pulse is a warning and the check
    /// says so.
    /// </summary>
    private async Task ReadPriorAsync()
    {
        _priorFailed = false;

        if (Row is null || _market is null || _outcome is null || _noGame)
        {
            _prior = null;
            _priorRead = false;
            return;
        }

        _priorRead = false;
        var (gameId, market, outcome) = (Row.Game.Id, _market, _outcome);

        // Its own catch rather than WithActivityAsync: a failed read here is not the window failing
        // to load, it is one check that cannot answer. The check says so (a warning, not a block),
        // where "reading" for ever would read as a check still on its way.
        Report(PulseState.Active);

        try
        {
            _prior = await Data.LatestOnSideAsync(gameId, market, outcome, DateTimeOffset.UtcNow - Checks.DuplicateWindow);
            _priorRead = true;
            Report(PulseState.Healthy);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The journal was not read for the duplicate check");
            _prior = null;
            _priorFailed = true;
            Report(PulseState.Warn, "journal unread");
        }
    }

    /// <summary>A team's name in the header was pressed: open that team's window.</summary>
    private void OpenTeam(Game game, int teamId)
        => Manager.OpenTeam(teamId, teamId == game.AwayTeamId ? game.AwayTeam?.Name : game.HomeTeam?.Name);
}
