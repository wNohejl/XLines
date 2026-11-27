using Microsoft.AspNetCore.Components;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.Changes;
using XLines.Data.CrossReference;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Web.Components.Desk;
using XLines.Web.Services;
using XLines.Web.Windowing;

namespace XLines.Web.Components.Panels;

/// <summary>
/// Line movement: how one side of a market moved at each book. This part holds the window's
/// parameters, its state, the aim Line watch gives it and the overview it shows before one;
/// the reads are in OddsPanel.Loading.cs and the movement as data in OddsPanel.Movement.cs.
/// </summary>
public partial class OddsPanel
{
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = default!;

    [Inject] private IServiceProvider Provider { get; set; } = default!;

    protected override IReadOnlySet<string> Watches { get; } = new HashSet<string> { DataTopics.Odds };

    protected override Task RefreshAsync() => LoadAsync();

    protected override string LoadSubject => "Line movement";

    /// <summary>The game to show: a press in Line watch, or a link from elsewhere; otherwise the next game to start.</summary>
    [Parameter] public int? GameId { get; set; }

    /// <summary>
    /// The market to show (<c>Markets.*</c>), from a price pressed on the Board in Line watch.
    /// Null keeps the market the window is on: a row press names only the game.
    /// </summary>
    [Parameter] public string? Market { get; set; }

    /// <summary>The side to show, as stored ("Kansas City Chiefs", "Over"). Null keeps the side, or the first.</summary>
    [Parameter] public string? Outcome { get; set; }

    /// <summary>
    /// The press that aimed the window: a token Line watch issues for every Board press, and for a
    /// game followed from the overview (LineWatch.Issue), so a press that names what the window was
    /// last aimed at still turns it back there after the operator has looked elsewhere in it, and a
    /// token not issued today (a desk saved on an earlier day) is not taken as today's choice
    /// (LineWatch.IsCurrent). Object, not string: whatever a saved desk restores, it reads as text.
    /// </summary>
    [Parameter] public object? Press { get; set; }

    /// <summary>
    /// What the window was last aimed at, the press included. Line watch re-aims the window already
    /// on the desk rather than opening another, so the window follows its parameters after the
    /// first load, not only at it; a render that hands the same aim back (the window redrawn for
    /// focus or layout) changes nothing the operator chose here.
    /// </summary>
    private (int? Game, string? Market, string? Outcome, string? Press)? _aimedAt;

    /// <summary>The window's reads (ILineMovementData, registered with the desk's services); a test hands it others.</summary>
    private ILineMovementData Reads => _reads ??= Provider.GetRequiredService<ILineMovementData>();

    private ILineMovementData? _reads;

    /// <summary>The board's row for the game: each side's best, fair price and every book's rung.</summary>
    private BoardRow? _board;

    /// <summary>This league's lines pull, as the job menu offers it; null before a read or with none.</summary>
    private XLines.Ingestion.Services.IngestionJob? _linesJob;

    /// <summary>Whether lines are scanned on a schedule; null when the setting could not be read.</summary>
    private bool? _scheduled;

    private DeskJobMenu? _pull;

    private List<Game> _games = [];
    private Game? _picked;
    private int? _gameId;
    private string _market = Markets.Moneyline;

    /// <summary>Every stored price on the chosen market, both sides. The side switch reads from it without a query.</summary>
    private List<OddsSnapshot> _snapshots = [];

    /// <summary>
    /// The outcome in focus, as stored. Kept across a market change when the new market has
    /// the same outcome — a team's moneyline and its spread — and reset to the first side
    /// when it does not.
    /// </summary>
    private string? _side;

    private IReadOnlyList<DeskSwitchOption<string>> _sideOptions = [];
    private IReadOnlyList<MovementRow> _rows = [];

    /// <summary>The books the desk polls (PolledBooks); empty when unknown, and the tables read as before.</summary>
    private IReadOnlyList<string> _polled = [];

    private string _headline = string.Empty;
    private string _span = string.Empty;

    private static readonly IReadOnlyList<DeskSwitchOption<string>> _marketOptions =
    [
        new(Markets.Moneyline, "Moneyline"),
        new(Markets.Spread, "Spread"),
        new(Markets.Total, "Total")
    ];

    /// <summary>
    /// The markets showing every book rather than the main sportsbooks, by key: the operator's choice per
    /// market, kept for as long as the window is open — across a side switch, a refresh, another
    /// game followed — and shared by the movement grid and the standing's books, which are the
    /// same books.
    /// </summary>
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    private void SetExpanded(bool open)
    {
        if (open)
            _expanded.Add(_market);
        else
            _expanded.Remove(_market);
    }

    private readonly string _booksId = "oddsbooks-" + Guid.NewGuid().ToString("N")[..8];

    private MatchupForm? _form;
    private int _windowDays = 30;

    /// <summary>
    /// The chosen game's sport, for a crest — null for a bout, whose fighters carry none, and
    /// before a game is chosen. Read off the game the window already holds with its sport.
    /// </summary>
    private string? CrestSport => _picked is { } game && !FightSports.IsFight(game.Sport?.Key) ? game.Sport?.Key : null;

    /// <summary>The chosen game's club by the name an outcome gives it, or null for Over, Under or a bout.</summary>
    private Team? Club(string? name)
    {
        if (CrestSport is null || _picked is not { } game || string.IsNullOrEmpty(name))
            return null;

        return string.Equals(name, game.AwayTeam?.Name, StringComparison.OrdinalIgnoreCase) ? game.AwayTeam
            : string.Equals(name, game.HomeTeam?.Name, StringComparison.OrdinalIgnoreCase) ? game.HomeTeam
            : null;
    }

    /// <summary>
    /// The games list and the first game's moves, as one load. The list is read inside it rather
    /// than ahead of it: a database that refused that first read threw out of the lifecycle and
    /// took the circuit — the whole desk — with it, and Try again has to repeat both.
    /// </summary>
    protected override Task OnInitializedAsync()
        => WithActivityAsync(async () =>
        {
            // Only games this reader has moves to see (ADR 0024): an evaluation feed's are the operator's.
            _games = (await Reads.GamesAsync()).ToList();

            _aimedAt = Aim;

            // In Line watch the window shows what the Board pressed, and nothing until it has: a
            // game a desk saved in an earlier run is not today's choice (LineWatch.IsCurrent).
            // Outside it, the window opens on the next game to start, as it always has.
            _following = !Watching || LineWatch.IsCurrent(Press);
            _gameId = !_following ? null : GameId ?? NextUp(_games, DateTimeOffset.UtcNow)?.Id;

            // An old aim restored with the desk leaves with it: the title no longer names the old
            // game over the overview, and the desk saves the overview, not the stale game.
            if (!_following)
                Remember([], null);
            _market = Market ?? _market;
            _side = Outcome ?? _side;

            await ReadAsync();
        }, Standing, StatusLine);

    /// <summary>
    /// The game the window opens on when nothing aimed it: the next to start, or the one that
    /// started last when none is ahead. It opened on the list's first, which was the oldest game
    /// on file once closing lines were kept — last April's, on a desk holding a season.
    /// </summary>
    public static Game? NextUp(IReadOnlyList<Game> games, DateTimeOffset now)
        => games.Where(g => g.StartsAt >= now).MinBy(g => g.StartsAt)
           ?? games.MaxBy(g => g.StartsAt);

    /// <summary>The parameters as one aim, the press as text so a saved desk's number compares.</summary>
    private (int? Game, string? Market, string? Outcome, string? Press) Aim
        => (GameId, Market, Outcome, Press?.ToString());

    /// <summary>
    /// Re-aimed in place (Line watch): the game, market and side a Board press names are shown as
    /// choosing them here would show them. Only a new aim does this — a press moves the count, so
    /// pressing the same price again after looking elsewhere here turns the window back to it —
    /// and a render handing the same aim back leaves what the operator chose here alone.
    /// </summary>
    protected override Task OnParametersSetAsync()
    {
        var aim = Aim;

        if (_aimedAt is null || _aimedAt == aim)
            return Task.CompletedTask;

        _aimedAt = aim;

        if (Watching && LineWatch.IsCurrent(Press))
            _following = true;
        else if (Overview)
            return Task.CompletedTask;

        if (Market is not null)
            _market = Market;

        if (Outcome is not null)
            _side = Outcome;

        if (GameId is { } id && id != _gameId)
            return OnGameChanged(id);

        return LoadAsync();
    }

    /// <summary>Whether the select has an option for this game, so it names it rather than its placeholder.</summary>
    private bool Listed(int? id)
        => id is not null && (_games.Any(g => g.Id == id) || _picked?.Id == id);

    private Task OnGameChanged(int? id)
    {
        _gameId = id;
        _following = true;

        return LoadAsync();
    }

    // ---- Line watch: the overview, and what is followed (ADR 0029) ------------------------

    /// <summary>Line watch is the workspace on the desk, and this window answers the Board.</summary>
    private bool Watching => LineWatch.Drives(Manager, WindowId);

    /// <summary>
    /// A game is being followed: pressed on the Board today, chosen from the overview, or picked in
    /// the window's own select. Outside Line watch the window always follows its game.
    /// </summary>
    private bool _following = true;

    /// <summary>Line watch with nothing chosen yet: the slate's overview stands where a game would.</summary>
    private bool Overview => Watching && !_following;

    private SlateOverview _slate = SlateOverview.Empty;

    /// <summary>A row of the overview pressed: that game, and from a move its market and side, followed here.</summary>
    private Task FollowAsync(Game game, string? market, string? outcome)
    {
        _following = true;
        _gameId = game.Id;

        if (market is not null)
            _market = market;

        _side = outcome;

        // Followed as a Board press is: in the window's parameters and title, so the window says
        // what it shows and a desk saved now comes back on this game, not the last one pressed.
        var aim = new Dictionary<string, object> { ["GameId"] = game.Id, ["Press"] = LineWatch.Issue() };
        if (market is not null)
            aim["Market"] = market;
        if (outcome is not null)
            aim["Outcome"] = outcome;

        Remember(aim, Matchup(game));
        return LoadAsync();
    }

    /// <summary>Back to the overview: nothing followed until the next press.</summary>
    private Task ShowOverviewAsync()
    {
        _following = false;
        _gameId = null;
        _picked = null;
        Remember([], null);
        return LoadAsync();
    }

    /// <summary>
    /// Writes what the window now follows into its own parameters and title, as LineWatch.Show
    /// does for a Board press: the title bar names it, and a saved desk keeps it. The aim is
    /// noted as this window's own, so the parameters coming back on the next render re-read nothing.
    /// The load that follows redraws the title bar (its pulse moves).
    /// </summary>
    private void Remember(Dictionary<string, object> aim, string? title)
    {
        _aimedAt = (aim.GetValueOrDefault("GameId") as int?, aim.GetValueOrDefault("Market") as string,
            aim.GetValueOrDefault("Outcome") as string, aim.GetValueOrDefault("Press")?.ToString());

        if (WindowId is null || Manager.Find(WindowId) is not { } window)
            return;

        WindowManager.Aim(window.Parameters, aim);
        window.TitleOverride = title;
    }

    private async Task ReadSlateAsync(CancellationToken ct)
    {
        SlateOverview slate;

        try
        {
            slate = await Reads.SlateAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _slate = slate;
        _rows = [];
        _snapshots = [];
        _sideOptions = [];
        _headline = string.Empty;
        _span = string.Empty;
    }

    /// <summary>An overview row read aloud: what it follows, and the move.</summary>
    private static string MoveLabel(SlateMove move)
        => $"Follow {Matchup(move.Game)}, {SelectionText.MarketWord(move.Market)}, {SideLabel(move.Row.Now.Outcome)}: "
           + $"{Quote(move.Row.Open)} to {Quote(move.Row.Now)} at {BookNames.Display(move.Row.Book)}";

    /// <summary>The chosen game with its teams and sport, so the sides can be put in matchup order.</summary>
    private async Task<Game?> PickedAsync(int? gameId, CancellationToken ct)
        => gameId is { } id ? await Reads.GameAsync(id, ct) : null;

    private async Task SetMarketAsync(string market)
    {
        _market = market;
        await LoadAsync();
    }

    /// <summary>The other side of what is already loaded: nothing is read again.</summary>
    private void SetSide(string side)
    {
        _side = side;
        Focus();
        Report(Standing(), StatusLine());
    }

    /// <summary>
    /// A new lookback is a load like the window's others, through the same rail, so a read that
    /// fails says so above the rosters already shown instead of ending the circuit. It read the
    /// form alone, and a full read already in flight could land after it with the old lookback's.
    /// </summary>
    private Task SetWindowAsync(int days)
    {
        _windowDays = days;

        return LoadAsync();
    }

    /// <summary>
    /// The cross-reference read. Separate from the odds read because the two answer different
    /// questions about the same game and only one of them changes when the market does.
    /// </summary>
    private async Task<MatchupForm?> ReadFormAsync(int? gameId, int days, CancellationToken ct)
    {
        if (gameId is not { } id)
            return null;

        return await Reads.FormAsync(id, TimeSpan.FromDays(days), ct);
    }

}
