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

// Line movement's reads: one load of the moves, the board's row, the polling setting, the
// league's pull and the form, handed over as each lands and only by the latest aim.
public partial class OddsPanel
{
    /// <summary>
    /// The read in flight, cancelled when the next starts. Board presses in Line watch re-aim the
    /// window faster than its reads come back: the generation guard in the panel base kept an older
    /// load from saying how the window stood, but not from handing over its rows, so the slower
    /// read landed last and drew one game's moves under another's name — and every superseded read
    /// ran to its end for nothing.
    /// </summary>
    private readonly LatestRead _latest = new();

    protected override void OnDispose()
    {
        _latest.Cancel();
        base.OnDispose();
    }

    /// <summary>
    /// The window's load: the moves on the chosen market and the form beside them, with the
    /// pulse showing activity and a failure kept to this window (PanelBase).
    /// </summary>
    private Task LoadAsync() => WithActivityAsync(ReadAsync, Standing, StatusLine);

    /// <summary>
    /// How the window stands once a load lands: idle with nothing to show, healthy with rows.
    /// The side switch asks the same thing without a load, so it is one answer for both.
    /// </summary>
    private PulseState Standing() => _rows.Count == 0 ? PulseState.Idle : PulseState.Healthy;

    /// <summary>The title bar's line: nothing before a game is chosen, "no moves", or the moves counted.</summary>
    private string? StatusLine()
        => _gameId is null ? null
            : _rows.Count == 0 ? "no moves"
            : MovesText(_rows.Sum(r => r.Moves));

    /// <summary>
    /// The window's reads, together, handed over in two steps: the moves (the game, its stored
    /// prices, the board's row, the polling setting) as soon as all four are in, then the league's
    /// pull and the form as each lands. They used to run one after another — six reads, the pull
    /// alone most of the wait — with nothing handed over until the last, so a Board press in Line
    /// watch renamed the window at once and left the last game's moves and rosters in it.
    /// Everything is read into locals first so a refresh of the same game that throws leaves the
    /// rows, the switches and the form as they were, its line above the moves the operator was
    /// reading rather than in place of them.
    ///
    /// <para>
    /// The game, market and lookback are taken once, as the read starts, and every read is of
    /// them. A newer aim cancels this read (<see cref="LatestRead.Next"/>); one that comes back anyway —
    /// a call that does not stop when asked — hands over nothing, so the window only ever shows
    /// what the latest aim read.
    /// </para>
    /// </summary>
    private async Task ReadAsync()
    {
        var ct = _latest.Next();

        // The books the desk polls, read again with every load: a key typed into Ops makes a
        // provider live without a restart (PolledBooks).
        _polled = PolledBooks.Read(ScopeFactory);

        if (Overview)
        {
            // Back to the overview while a game's moves were still coming: that read was just
            // cancelled and will not clear the loading shape it put up.
            _turning = false;
            await ReadSlateAsync(ct);
            return;
        }
        var gameId = _gameId;
        var market = _market;
        var days = _windowDays;

        // Another game than the one held: what is on screen is the last game's, and the title
        // and the picker already name this one. The body waits in its loading shape until this
        // game's moves land, rather than showing one game's rows and rosters under another's
        // name for as long as the reads take — which is what a Board press in Line watch did.
        var turned = gameId is not null && _picked?.Id != gameId;

        if (turned)
        {
            _turning = true;
            _form = null;
            _board = null;
            _snapshots = [];
            _sideOptions = [];
            _rows = [];
            _headline = string.Empty;
            _span = string.Empty;
        }

        Game? picked;
        List<OddsSnapshot> snapshots = [];
        bool? scheduled = null;
        BoardRow? board;

        // Every read has a context or a scope of its own, and none needs another's answer, so
        // they go out together: the wait is the slowest read's, not the sum of six. The form is
        // the slow one — two rosters' stat lines — and the moves are the window's subject, so
        // the moves are handed over as soon as they are in and the form when it follows.
        var formRead = ReadFormAsync(gameId, days, ct);
        Quiet(formRead);

        try
        {
            var pickedRead = _picked is { } held && held.Id == gameId ? Task.FromResult<Game?>(held) : PickedAsync(gameId, ct);
            var boardRead = ReadBoardAsync(gameId, ct);

            var snapshotsRead = gameId is { } id
                ? Reads.SnapshotsAsync(id, market, ct)
                : Task.FromResult<IReadOnlyList<OddsSnapshot>>([]);

            // What could move these numbers next: the operator's polling setting, as Ops sets it.
            var scheduledRead = gameId is null ? Task.FromResult<bool?>(null) : ScheduledAsync(ct);

            await Task.WhenAll(pickedRead, boardRead, snapshotsRead, scheduledRead);

            picked = await pickedRead;
            board = await boardRead;
            snapshots = (await snapshotsRead).ToList();
            scheduled = await scheduledRead;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded: the newer aim's read hands over, and its load says how the window stands.
            return;
        }
        catch
        {
            // This game's moves did not come. The failure line says so; the loading shape under
            // it would say the window was still asking.
            if (!ct.IsCancellationRequested)
                _turning = false;

            throw;
        }

        if (ct.IsCancellationRequested)
            return;

        HandOverMoves(picked, snapshots, scheduled, board, market);

        // The league's pull, by the game's own sport (the board's row names it too), and the
        // form, each drawn as it lands. A newer aim between them takes the window: nothing
        // older is handed over after it.
        var jobRead = ReadLinesJobAsync((picked?.Sport ?? board?.Game.Sport)?.Key, ct);
        Quiet(jobRead);

        StateHasChanged();

        try
        {
            var linesJob = await jobRead;

            if (ct.IsCancellationRequested)
                return;

            if (!Equals(_linesJob, linesJob))
            {
                _linesJob = linesJob;
                StateHasChanged();
            }

            var form = await formRead;

            if (ct.IsCancellationRequested)
                return;

            _form = form;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The window was turned to another game and that game's moves are not in yet: the body holds
    /// its loading shape, as it does before the first load.
    /// </summary>
    private bool _turning;

    /// <summary>
    /// Moves have been handed over at least once. The window is not Loaded until its whole load
    /// is back, the form included, so on first open the loading shape stood above the moves it
    /// was waiting for; it steps aside as soon as there are moves to show.
    /// </summary>
    private bool _movesIn;

    /// <summary>
    /// A read left in flight when a newer aim took the window, or when another read of the same
    /// load threw first, is never awaited; its fault is looked at here so it is not reported as
    /// an exception nobody observed.
    /// </summary>
    private static void Quiet(Task read)
        => read.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task<bool?> ScheduledAsync(CancellationToken ct) => await Reads.ScheduledAsync(ct);

    /// <summary>
    /// The first hand-over: the game, its moves on the market, the board's row and the polling
    /// setting, with the rows and the headline built for the side in focus.
    /// </summary>
    private void HandOverMoves(Game? picked, List<OddsSnapshot> snapshots, bool? scheduled, BoardRow? board, string market)
    {
        _turning = false;
        _movesIn = true;
        _picked = picked;
        _snapshots = snapshots;
        _scheduled = scheduled;
        _board = board;

        // The pull is a league's: kept across games of one league, so its key does not blink
        // out and back on every press, and dropped for another's until that one is read.
        if (_linesJob is not null && !string.Equals(_linesSport, (picked?.Sport ?? board?.Game.Sport)?.Key, StringComparison.OrdinalIgnoreCase))
            _linesJob = null;

        _linesSport = (picked?.Sport ?? board?.Game.Sport)?.Key;
        _sideOptions = [];
        _rows = [];
        _headline = string.Empty;
        _span = string.Empty;

        if (_snapshots.Count == 0)
            return;

        var sides = Sides(_snapshots.Select(s => s.Outcome), market,
            _picked?.AwayTeam?.Name, _picked?.HomeTeam?.Name);

        // Short names unless two clubs share one: the White Sox and the Red Sox are both "Sox".
        var shortNames = sides.Select(SideLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count() == sides.Count;

        _sideOptions = sides
            .Select(o => new DeskSwitchOption<string>(o, shortNames ? SideLabel(o) : o, o.Contains(' ') ? o : null))
            .ToList();

        // The side asked for, as stored: a press on the Board names it as the board reads it, and
        // a provider's "over" is the board's "Over".
        _side = sides.FirstOrDefault(s => string.Equals(s, _side, StringComparison.OrdinalIgnoreCase)) ?? sides[0];

        Focus();
    }

    /// <summary>
    /// The board's row for the game — each side's best, its fair price, every book's rung, all to
    /// the reader's tier (BoardService, ADR 0024) — and the league's lines pull with its cost. Read
    /// through the wager seam the Place wager window reads them through. A row that will not come
    /// leaves the standing to say less, not the window to fail: the moves are the window's subject.
    /// </summary>
    private async Task<BoardRow?> ReadBoardAsync(int? gameId, CancellationToken ct)
    {
        if (gameId is not { } id || Provider.GetService<IWagerData>() is not { } data)
            return null;

        try
        {
            return await data.RowAsync(id, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.LogWarning(ex, "The board row for Line movement's standing was not read");
            return null;
        }
    }

    /// <summary>The league whose pull <see cref="_linesJob"/> is, so another league's game drops it.</summary>
    private string? _linesSport;

    /// <summary>The league's lines pull as the job menu offers it; null for a visitor, or where it will not come.</summary>
    private async Task<XLines.Ingestion.Services.IngestionJob?> ReadLinesJobAsync(string? sportKey, CancellationToken ct)
    {
        if (sportKey is null || !Manager.Access.CanOperate || Provider.GetService<IWagerData>() is not { } data)
            return null;

        try
        {
            return await data.LinesJobAsync(sportKey, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.LogWarning(ex, "The lines pull for Line movement's standing was not read");
            return null;
        }
    }

    /// <summary>Whether any book on the side in focus has moved since it opened.</summary>
    private bool AnyMoves => _rows.Any(r => r.Moves > 0);

    /// <summary>The market in focus on the board's row.</summary>
    private MarketPair? Pair => _board is not { } row ? null
        : row.Moneyline.Market == _market ? row.Moneyline
        : row.Spread.Market == _market ? row.Spread
        : row.Total.Market == _market ? row.Total
        : null;

    private BestOffer? SideOffer => OfferFor(_side);

    private BestOffer? OtherOffer => OfferFor(OtherSide);

    private BestOffer? OfferFor(string? outcome)
        => outcome is null || Pair is not { } pair ? null
            : new[] { pair.First, pair.Second }.FirstOrDefault(o => o is not null
                && string.Equals(o.Outcome, outcome, StringComparison.OrdinalIgnoreCase));

    /// <summary>The market's other side, from the switch's sides; null on a market priced on one side.</summary>
    private string? OtherSide
        => _sideOptions.Select(o => o.Value).FirstOrDefault(s => !string.Equals(s, _side, StringComparison.OrdinalIgnoreCase));

    /// <summary>A side as the switch names it: "Chiefs", "Over".</summary>
    private static string SideName(string? outcome) => outcome is null ? string.Empty : SideLabel(outcome);

    /// <summary>"Pull NFL lines · 2 credits": the job menu's own entry, at the cost it quotes; null where it cannot run.</summary>
    private string? PullLabel
        => _linesJob is { Available: true } job && _board?.Game.Sport is { } sport
            ? job.EstimatedCredits > 0
                ? $"Pull {sport.Key.ToUpperInvariant()} lines · {job.EstimatedCredits} {(job.EstimatedCredits == 1 ? "credit" : "credits")}"
                : $"Pull {sport.Key.ToUpperInvariant()} lines"
            : null;

    /// <summary>Why the pull cannot run, in the job's words (no key, the month spent).</summary>
    private string? PullUnavailable => _linesJob is { Available: false } job ? job.Unavailable : null;

    /// <summary>
    /// Presses the toolbar's job menu on this league's pull, so the press here and the entry there
    /// are one run: the same confirm before credits are spent, the same busy key, the same toast.
    /// </summary>
    private Task PullAsync()
        => _pull is not null && _linesJob is { } job ? _pull.RunAsync(job.Key) : Task.CompletedTask;

    /// <summary>
    /// Builds the rows and the headline for the side in focus. The window's status is not said
    /// here: a load says it as it settles (<see cref="StatusLine"/>), the side switch after this.
    /// </summary>
    private void Focus()
    {
        _rows = _side is null ? [] : MovementRows(_snapshots, _side);

        if (_rows.Count == 0)
        {
            _headline = string.Empty;
            _span = string.Empty;
            return;
        }

        _headline = Headline(_rows, _market, _side!);
        _span = SpanText(_rows);
    }

}
