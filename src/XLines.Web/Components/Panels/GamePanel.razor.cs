using Microsoft.AspNetCore.Components;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data.Changes;
using XLines.Data.CrossReference;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Web.Components.Desk;
using XLines.Web.Components.Snippets;
using XLines.Web.Windowing;

namespace XLines.Web.Components.Panels;

/// <summary>
/// The Game window's state, its load and what its rows do: the narrowed notices, the season, the
/// open market and roster row, the lines' wording and the price door. The markup and its
/// fragments are <c>GamePanel.razor</c>; the bet rows are <c>GameBetRows.razor</c>.
/// </summary>
public partial class GamePanel : PanelBase
{
    [Inject] private XLines.Web.Services.IGameWindowData Data { get; set; } = default!;
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = default!;

    /// <summary>The bets card's heading, which names its section; one per window.</summary>
    private string BetsHeadingId => $"gamebets-{_uid}";

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];

    protected override string LoadSubject => "The game";

    protected override IReadOnlySet<string> Watches { get; } = new HashSet<string> { DataTopics.Games, DataTopics.Odds, DataTopics.Journal };

    /// <summary>
    /// <c>games</c> and <c>odds</c> for this game only: a score moving in one of the other
    /// thirteen games on a Sunday is not this window's business (spec 2026-10-24 §5 item 4). The
    /// journal stays bare — a bet saved anywhere may be on this game. Built when the game is set,
    /// since the Desk reads it on the publisher's thread for every notice.
    /// </summary>
    protected override IReadOnlyDictionary<string, IReadOnlySet<string>> WatchesIds => _ids;

    private IReadOnlyDictionary<string, IReadOnlySet<string>> _ids = new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>This game's id as a notice addresses it.</summary>
    private string _gameKey = string.Empty;

    /// <summary>
    /// Reloads what the notice touched, and only that (spec 2026-10-24 §5 item 5).
    ///
    /// <para>
    /// The window used to reload all of itself on every notice — ten queries, the pulse going
    /// active and the grids repainting, to move two digits — and that reload was most of why a
    /// live game read as blocky. Now <c>games:{id}</c> reads the game alone (one query) and
    /// re-renders the title; <c>odds:{id}</c>, or a bare <c>odds</c>, reads the row with its
    /// prices. The bets, the form, the rosters and the box score are read again only on a
    /// <c>journal</c> notice, on a bare <c>games</c> — a box-score write or the fallback poll,
    /// which do not say which game — when the game has just gone final, when a final's box score
    /// is still owed, or when the refresh does not say what changed. The live box score is the gamecast's job (§6), not this one's.
    /// </para>
    ///
    /// <para>
    /// The row reload does not raise the pulse: the window is not loading, it is keeping up. A
    /// failure leaves the loaded game where it is, and the pulse says the refresh failed.
    /// </para>
    /// </summary>
    protected override async Task RefreshAsync(IReadOnlySet<string> heard)
    {
        if (_row is null || Failed || heard.Count == 0
            || heard.Contains(DataTopics.Games) || heard.Contains(DataTopics.Journal))
        {
            await LoadAsync();
            return;
        }

        var wasFinal = _row.Game.Status == GameStatus.Final;
        var prices = heard.Contains(DataTopics.Odds)
                     || heard.Contains(DataTopics.Address(DataTopics.Odds, _gameKey));

        var row = prices
            ? await Data.RowAsync(GameId)
            : await Data.GameAsync(GameId) is { } game ? _row with { Game = game } : null;

        // A final with no box score yet is waiting on the results sweep, whose box-score write is
        // addressed to this game hours after the live pass landed the final. Read the detail
        // again on it, or the box never appears in a window left open; a live game stays on the
        // row alone.
        var boxOwed = row?.Game.Status == GameStatus.Final && !HasBox
                      && heard.Contains(DataTopics.Address(DataTopics.Games, _gameKey));

        if (row is null || (!wasFinal && row.Game.Status == GameStatus.Final) || boxOwed)
        {
            await LoadAsync();
            return;
        }

        _row = row;
        Report(PulseState.Healthy, StateStatus(row.Game));
    }

    /// <summary>
    /// The game this view is about. Taken as an id rather than an object so the view can be
    /// hosted as a desk window, which is rebuilt from its parameters and cannot carry a
    /// reference.
    /// </summary>
    [Parameter, EditorRequired] public int GameId { get; set; }

    private BoardRow? _row;
    private IReadOnlyList<JournalEntry> _bets = [];

    private MatchupForm? _matchup;
    private TeamForm? _homeTeam;
    private TeamForm? _awayTeam;

    /// <summary>Which of a fight's two wide readings is on screen. See <see cref="FightSections"/>.</summary>
    private enum Section { Tape, Stats }

    private Section _section = Section.Tape;

    private bool HasBox => _box is not null && !_box.IsEmpty;

    private bool HasRoster => _matchup is not null && !_matchup.IsEmpty;

    private bool HasTeamData
        => (_homeTeam?.Recent.Count ?? 0) > 0 || (_awayTeam?.Recent.Count ?? 0) > 0;

    /// <summary>
    /// A fight's wide readings (ADR 0021): no team data or rosters — the two fighters side by
    /// side, and once it is over, each one's fight stats. The lines are a card of the overview
    /// above, for a fight as for a game, so they are no tab. A game has one wide reading (the
    /// players, or the box score), which needs no tabs at all.
    /// </summary>
    private IReadOnlyList<DeskTab<Section>> FightSections =>
    [
        new(Section.Tape, "Tale of the tape"),
        new(Section.Stats, "Fight stats", HasBox)
    ];

    /// <summary>The card and result when this game is a fight; null for a team sport.</summary>
    private Bout? _bout;

    /// <summary>Where a bout sits and how far it goes: "Main Card · Lightweight · 5 rounds".</summary>
    private static string BoutFrame(Bout bout)
        => string.Join(" · ", new[]
        {
            bout.MatchNumber == 1 ? "Main event" : bout.CardSegment,
            bout.WeightClass,
            // Zero is a card a visitor is not shown (ADR 0024): no distance rather than a guessed one.
            bout.ScheduledRounds > 0 ? $"{bout.ScheduledRounds} rounds" : null
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>This game's box score. Null until the game has produced one.</summary>
    private GameBoxScore? _box;

    /// <summary>
    /// The season the team data and rosters are drawn from — the game's own where it has
    /// games behind it, the one before for a preview. Null only when neither side has a
    /// season on record, where the readers fall back to a wide rolling window.
    /// </summary>
    private int? _seasonYear;
    private IReadOnlyList<int> _seasons = [];
    private IReadOnlyList<DeskSwitchOption<int>> _seasonOptions = [];

    /// <summary>The roster row showing its recent games. One at a time, like every other row.</summary>
    private int? _openPlayer;

    private void TogglePlayer(int playerId)
        => _openPlayer = _openPlayer == playerId ? null : playerId;

    /// <summary>
    /// What an open roster row offers.
    ///
    /// <para>
    /// One action, because there is one question. The averages in the row are what these games
    /// produced, and the mean hides the shape — four quiet games and one enormous one read the
    /// same as five steady ones. So the snippet is the games themselves, and the escape is the
    /// player's own window for anyone who wants more than five.
    /// </para>
    /// </summary>
    private List<RowAction> ActionsFor(PlayerForm player)
    {
        var id = player.PlayerId;
        var name = player.Name;

        return
        [
            new RowAction
            {
                Key = "recent",
                Label = "Last 5",
                Icon = DeskIcons.Timeline,
                Snippet = RecentSnippet(id),
                OpenLabel = "Player window",
                OpenWindow = EventCallback.Factory.Create(this, () => Manager.OpenPlayer(id, name, WindowId))
            }
        ];
    }

    protected override async Task OnParametersSetAsync()
    {
        var key = GameId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (key != _gameKey)
        {
            _ids = NoticeTokens.Narrow([GameId], DataTopics.Games, DataTopics.Odds);
            _gameKey = key;
            _expandedMarkets.Clear();
        }

        await LoadAsync();
    }

    private async Task SetSeasonAsync(int year)
    {
        _seasonYear = year;
        await LoadAsync();
    }

    /// <summary>
    /// The load in flight, cancelled by the next. The base class's generation check kept a
    /// superseded load from saying how the window stood, but not from handing over what it read,
    /// so a slower read of the last season or game could land last and paint it under the new one.
    /// </summary>
    private readonly LatestRead _reads = new();

    protected override void OnDispose() => _reads.Cancel();

    /// <summary>
    /// The row, then the detail it names, into locals and handed over at once, and only by the
    /// latest load; a failed detail read leaves the row that was on screen with the rest. The
    /// detail needs the row's game, so the two stay one after another. The token is taken inside
    /// the work so Try again, which repeats it, starts a read of its own.
    /// </summary>
    private async Task LoadAsync()
    {
        await WithActivityAsync(async () =>
        {
            var ct = _reads.Next();

            await LatestRead.HandOverAsync(ct, async () =>
            {
                var row = await Data.RowAsync(GameId, ct);

                // The operator's own bets come with the rest. A visitor's desk never reads the
                // journal, so there is nothing personal to leak into a window it may see (ADR 0018).
                // The season switched to is kept while either side has played in it; otherwise the
                // game's own, or for a preview of an unplayed season the one before.
                var detail = row is null
                    ? null
                    : await Data.DetailAsync(row.Game, _seasonYear, withBets: Manager.Access.CanOperate, ct);

                return (Row: row, Detail: detail);
            },
            read => HandOver(read.Row, read.Detail));
        },
        settle: () => _row is null ? PulseState.Idle : PulseState.Healthy,
        status: () => _row is null ? "not found" : StateStatus(_row.Game));
    }

    /// <summary>
    /// What the latest load read: the row, missing or not, and then the detail it names. A game
    /// not found hands over its missing row and leaves the rest as it was.
    /// </summary>
    private void HandOver(BoardRow? row, XLines.Web.Services.GameWindowDetail? detail)
    {
        _row = row;
        _polled = XLines.Web.Services.PolledBooks.Read(ScopeFactory);

        if (_row is null || detail is null)
            return;

        _bout = detail.Bout;
        _bets = detail.Bets;
        _seasons = detail.Seasons;
        _seasonYear = detail.SeasonYear;
        _box = detail.Box;
        _matchup = detail.Matchup;
        _homeTeam = detail.HomeTeam;
        _awayTeam = detail.AwayTeam;

        _seasonOptions = SeasonOptions(_row.Game.Sport?.Key, _seasons);
    }

    private static DeskState StatusState(GameStatus status) => status switch
    {
        GameStatus.Live => DeskState.Warning,
        GameStatus.Final => DeskState.Positive,
        _ => DeskState.Info
    };

    /// <summary>The lines card's heading, which names its section; one per window.</summary>
    private string LinesHeadingId => $"gamelines-{_uid}";

    /// <summary>The team data card's heading.</summary>
    private string TeamsHeadingId => $"gameteams-{_uid}";

    /// <summary>The market open under its row, by key; null when every row is closed.</summary>
    private string? _openMarket;

    private void ToggleMarket(string market) => _openMarket = _openMarket == market ? null : market;

    /// <summary>
    /// The markets showing every book rather than the main sportsbooks (MarketMovement's More chip),
    /// kept by the window so a market closed and opened again comes back as it was left. Cleared
    /// when the window is aimed at another game: its books are another market's.
    /// </summary>
    private readonly HashSet<string> _expandedMarkets = [];

    private void SetMarketExpanded(string market, bool all)
    {
        if (all)
            _expandedMarkets.Add(market);
        else
            _expandedMarkets.Remove(market);
    }

    /// <summary>From this many bets the rows fold behind one line; two rows fold to nothing smaller.</summary>
    private const int FoldBetsAt = 3;

    private bool _betsOpen;

    /// <summary>
    /// The folded bets' one line: what is open and what is riding, then what has settled —
    /// "3 pending · $335 at risk · 1 won".
    /// </summary>
    public static string BetsSummary(IReadOnlyList<JournalEntry> bets)
    {
        var pending = bets.Where(b => b.Result == EntryResult.Pending).ToList();
        var parts = new List<string>();

        if (pending.Count > 0)
            parts.Add($"{pending.Count} pending · {OddsMath.Money(pending.Sum(b => b.IsParlayLeg ? 0 : b.Stake))} at risk");

        foreach (var (result, word) in new[] { (EntryResult.Win, "won"), (EntryResult.Loss, "lost"), (EntryResult.Push, "pushed"), (EntryResult.Void, "void") })
        {
            var n = bets.Count(b => b.Result == result);
            if (n > 0)
                parts.Add($"{n} {word}");
        }

        return parts.Count == 0 ? $"{bets.Count} bets" : string.Join(" · ", parts);
    }

    /// <summary>A row's market, as the wager window names it.</summary>
    private static string MarketKey(string row) => row switch
    {
        "Moneyline" => Markets.Moneyline,
        "Spread" => Markets.Spread,
        _ => Markets.Total
    };

    /// <summary>A live price the operator can take: a closing number is history, not a bet.</summary>
    private bool CanTake(BestOffer? offer) => offer is { IsClosing: false } && Manager.Access.CanOperate;

    /// <summary>
    /// A price pressed: the wager window on this game, landed on that side at the book holding
    /// the best number — the Board's price door, from the Game window.
    /// </summary>
    private void Take(string market, BestOffer? offer)
    {
        if (!CanTake(offer) || _row?.Game is not { } game)
            return;

        Manager.OpenWager(game, market, offer!.Outcome, offer.Book);
    }

    /// <summary>Every book's number on a side, by name — the cell's tooltip: the main sportsbooks first in their fixed order, then the rest as the board ranks them.</summary>
    private static string EveryBook(BestOffer offer)
        => string.Join("\n", offer.Rungs.OrderBy(r => BookNames.MainRank(r.Book) ?? int.MaxValue).Select(r =>
            $"{BookNames.Display(r.Book)} {(r.Line is { } l ? OddsMath.FormatLine(l, offer.Outcome) + " " : "")}{OddsMath.FormatPrice(r.PriceAmerican)}"));

    /// <summary>A side as the lines name it: the club's nickname, as the Board's matchup does.</summary>
    private string SideName(bool home)
    {
        var team = home ? _row?.Game.HomeTeam : _row?.Game.AwayTeam;
        return TeamNames.Short(team, Scoreline.Abbrev(team, home ? "Home" : "Away"));
    }

    /// <summary>A side's abbreviation as ESPN spells it, for its crest.</summary>
    private string? SideAbbrev(bool home) => (home ? _row?.Game.HomeTeam : _row?.Game.AwayTeam)?.Abbrev;

    /// <summary>The group's footer: what the numbers are, and when a close, what that means.</summary>
    private string LinesFoot
        => _row!.PricesAreClosing
            ? $"Closing prices: the last number each book showed before the start; {MainText(_row, _polled)}. Open a market for every book's."
            : $"The best price on each side across {AcrossText(_row, _polled)}; {MainText(_row, _polled)}. Open a market for every book's, and how each moved.";

    /// <summary>
    /// The direct count of the main sportsbooks over the game's markets (MainCoverage): "4 of 5 main
    /// sportsbooks priced (missing Caesars)".
    /// </summary>
    public static string MainText(BoardRow row, IReadOnlyCollection<string>? polled)
        => MainCoverage.Of(BookCoverage.Books(row), polled).Phrase;

    /// <summary>
    /// "8 of 10 polled books" once the polled list is known (a book outside it that priced the game
    /// is added: "8 of 10 polled books and 1 more"); "8 books", as before, while it is not.
    /// </summary>
    public static string AcrossText(BoardRow row, IReadOnlyCollection<string>? polled)
    {
        if (BookCoverage.Of(BookCoverage.Books(row), polled) is not { } coverage)
            return $"{row.BookCount:N0} {(row.BookCount == 1 ? "book" : "books")}";

        var extra = coverage.Priced - coverage.PolledPriced;
        return $"{coverage.PolledPriced} of {coverage.Polled} polled {(coverage.Polled == 1 ? "book" : "books")}"
               + (extra > 0 ? $" and {extra} more" : "");
    }

    /// <summary>The books the desk polls (PolledBooks), read with each load; empty when unknown.</summary>
    private IReadOnlyList<string> _polled = [];

    /// <summary>"2026 season", or "2025 postseason" for a February game that closes the prior year.</summary>
    private static string SeasonWords(Game game)
        => $"{SeasonCalendar.Label(game.Sport?.Key ?? string.Empty, game.SeasonYear)} "
           + (game.SeasonType == SeasonType.Postseason ? "postseason" : "season");

    /// <summary>A player followed from the box score (BoxScoreSide), opened beside this window.</summary>
    private Task OpenBoxPlayer((int PlayerId, string Name) player)
    {
        Manager.OpenPlayer(player.PlayerId, player.Name, WindowId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A side split into units — Passing, Rushing, Receiving, Defense, Special teams — each with
    /// columns of its own; one table, as before, where the sport's lines name no groups.
    /// </summary>
    private static IEnumerable<(string? Unit, IReadOnlyList<PlayerForm> Players)> Units(IReadOnlyList<PlayerForm> side)
    {
        var units = side.Select(p => (Unit: StatColumns.UnitOf(p.Totals), Player: p)).ToList();

        if (units.All(u => u.Unit is null))
            return [(null, side)];

        return units
            .GroupBy(u => u.Unit)
            .OrderBy(g => StatColumns.UnitOrder(g.Key))
            .Select(g => (g.Key, (IReadOnlyList<PlayerForm>)g.Select(u => u.Player).ToList()));
    }

    private static string Titled(string team, string? unit) => unit is null ? team : $"{team} · {unit}";
}
