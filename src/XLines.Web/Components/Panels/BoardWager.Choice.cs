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

public partial class BoardWager
{
    /// <summary>Opened without a game: the Journal's door. The search stays, so the game can be changed.</summary>
    private bool Searching => GameId is null;

    private SelectionChoice? Side
        => _market is null || _outcome is null
            ? null
            : SelectionChoice.All(Row).FirstOrDefault(c => c.Market == _market
                                                          && string.Equals(c.Outcome, _outcome, StringComparison.OrdinalIgnoreCase));

    private bool HasSide => _noGame || Side is not null;

    /// <summary>The price comes from the fields: asked for, or nothing fetched to take it from.</summary>
    private bool Typing => _manual || _noGame || (Side is { Offer: null });

    private bool HasLine => !_noGame && _market is Markets.Spread or Markets.Total;

    /// <summary>
    /// Where each step stands. Done is the step's own fact — a side, a price, a stake; the step
    /// to do is the first one that is not done, and everything after it waits. Review is never
    /// done: it is where the window ends. With no game yet, every step waits.
    /// </summary>
    private DeskStepState StateOf(int step)
    {
        if (Row is null && !_noGame)
            return DeskStepState.Todo;

        return Done[step - 1] ? DeskStepState.Done
            : step == Current ? DeskStepState.Current
            : DeskStepState.Todo;
    }

    /// <summary>
    /// The bet step is done when it has a price: a side picked but still being priced keeps the
    /// step open, since the price is the step's second half.
    /// </summary>
    private bool[] Done =>
    [
        _offer is not null,
        BuildParlay || _stake > 0,
        false
    ];

    private int Current => Array.IndexOf(Done, false) + 1;

    /// <summary>
    /// The step to do is open, and a finished one the operator asked for. Two more stay open:
    /// the stake beside the review, and the review as soon as there is a price — its checks are
    /// worth reading before a stake is typed, and the Log key says what it still needs. A bet on
    /// no game is typed from end to end, so none of its steps fold: a step that folded on the
    /// first character would take the field away mid-word.
    /// </summary>
    private bool IsOpen(int step)
    {
        if (step == ReviewStep)
            return _offer is not null;

        return StateOf(step) != DeskStepState.Todo
               && (_noGame || _opened == step || step == Current || (step == StakeStep && Current == ReviewStep));
    }

    private void OpenStep(int step) => _opened = step;

    /// <summary>
    /// The one place the price is decided. Typed: the fields. Otherwise the chosen book's latest
    /// rung where it quotes the side, else the best price on it.
    /// </summary>
    private void Reprice()
    {
        if (Typing)
        {
            var side = Side;
            var market = _noGame ? "other" : side?.Market;
            var outcome = _noGame ? string.Empty : side?.Outcome;

            _offer = !_noGame || !string.IsNullOrWhiteSpace(_described)
                ? _typed.Offer(market, outcome, HasLine, side?.Offer, DateTimeOffset.UtcNow, _noGame ? _described!.Trim() : null)
                : null;
            return;
        }

        if (Side is not { Offer: { } quoted } chosen)
        {
            _offer = null;
            return;
        }

        _offer = (_book is not null ? WagerOffer.AtBook(chosen.Market, quoted, _book) : null)
                 ?? WagerOffer.Best(chosen.Market, quoted);
    }

    private async Task PickGameAsync(Game? game)
    {
        _gameId = game?.Id;
        _market = null;
        _outcome = null;
        _manual = false;
        _moved = null;
        _logged = null;
        _receipt = null;
        _offer = null;
        _opened = null;
        _sideChosen = false;
        SyncUnsaved();

        if (game is null)
        {
            Row = null;
            return;
        }

        _noGame = false;
        await ReadRowAsync();

        // Searched for, not opened from a row: nothing says which side was bet, so none is assumed.
        await ReadPriorAsync();
    }

    private void SetNoGame(bool noGame)
    {
        _noGame = noGame;
        _logged = null;
        _opened = null;

        if (noGame)
        {
            Row = null;
            _gameId = null;
            _market = null;
            _outcome = null;
            _typed.Restore(_typed.Price, _typed.Line, _typed.BookKey ?? _book);
        }

        Reprice();
    }

    private void SetDescribed(string? text)
    {
        _described = text;
        // Describing the next bet is beginning it: the last one's line has been read.
        _receipt = null;
        Reprice();
    }

    private async Task ChooseAsync(SelectionChoice choice)
    {
        // A side picked is the next bet begun: the logged one's line goes.
        _receipt = null;
        _market = choice.Market;
        _outcome = choice.Outcome;
        _moved = null;
        // Chosen: the step folds and the next one opens.
        _opened = null;
        _sideChosen = true;
        SyncUnsaved();

        // A price typed over the last side's close was that side's; this side starts afresh.
        if (_fromClose)
        {
            _manual = false;
            _fromClose = false;
        }

        _keptClose = false;

        if (choice.Offer is null)
            Prefill(null);

        Reprice();
        TypeOverClose();
        await ReadPriorAsync();
    }

    /// <summary>
    /// A different book is a different bet: the stake and the note typed for the old one go, so
    /// a $110 meant for DraftKings is never logged at FanDuel by a press that did not look down.
    /// Choosing the book already chosen changes nothing.
    /// </summary>
    private void ChooseBook(string book)
    {
        if (!string.Equals(_offer?.Book, book, StringComparison.OrdinalIgnoreCase))
        {
            _stake = 0;
            _note = null;
        }

        _book = book;
        _moved = null;
        _opened = null;
        Reprice();
        SyncUnsaved();
    }

    /// <summary>
    /// Into a typed price starting from the number on screen, so changing -166 to -160 is one edit
    /// rather than three fields; back out to the fetched price, untouched.
    /// </summary>
    /// <summary>
    /// A game that has started has only its close to show, and a close could not have been
    /// taken: the window refused it and pointed at the pencil, so a parlay of Sunday's bets,
    /// logged on Monday, could not be built here at all. A bet on a started game was placed
    /// earlier at a price the operator knows, so the price now opens typed, starting from the
    /// close, and a sentence under the card says to make it the one they got — the pencil,
    /// pressed for them. Once per side: going back to the fetched price leaves it there, closing
    /// note and all.
    /// </summary>
    private void TypeOverClose()
    {
        if (_offer is not { IsClosing: true, Manual: false } closing || _keptClose)
            return;

        _manual = true;
        _fromClose = true;
        // The step stays open on the price, so the number to change is in view, not folded.
        _opened = BetStep;
        Prefill(closing);
        Reprice();
    }

    // The price was typed for the operator from a started game's close (TypeOverClose).
    private bool _fromClose;

    // The operator went back to the fetched close on this side, so it is not typed over again.
    private bool _keptClose;

    private void ToggleManual()
    {
        _manual = !_manual;
        _keptClose = !_manual && _fromClose;
        _fromClose = false;
        _moved = null;
        // The price is being worked on: keep its step open whichever way the toggle went.
        _opened = BetStep;

        if (_manual)
            Prefill(_offer);

        Reprice();
    }

    private void Prefill(WagerOffer? from) => _typed.StartFrom(from, _book);

    private void SetStake(decimal stake)
    {
        _stake = stake;
        SyncUnsaved();
    }

    /// <summary>
    /// A side the operator picked, as opposed to one a door landed on: a window opened from a
    /// Board row already shows a side, and closing it loses nothing the operator chose.
    /// </summary>
    private bool _sideChosen;

    /// <summary>
    /// Tells the window whether it holds a wager the operator has started — a side picked or a
    /// stake set — so closing or evicting it asks first. Cleared when the wager is logged and the
    /// wizard resets, and when a new game is searched for. Banking on the slip or as a leg leaves
    /// the bet on screen, so it leaves the mark too: a window marked clean after Add to slip could
    /// be evicted and come back empty, the bet the operator was still looking at gone.
    /// </summary>
    private void SyncUnsaved() => MarkUnsaved(_sideChosen || _stake > 0, "this wager");
}
