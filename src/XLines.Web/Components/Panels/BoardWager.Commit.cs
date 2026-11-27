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
    /// <summary>
    /// A second window on this bet through the door the Journal's Duplicate uses: the side, the
    /// book, the stake and the note as they stand here, and the price only if it was typed.
    /// </summary>
    private void Duplicate()
    {
        if (_offer is not { } offer)
            return;

        Manager.OpenWagerCopy(WagerPrefill.From(offer, Row?.Game.Id, _stake, _note), Row is null ? null : Matchup(Row.Game));
    }

    private bool CanLog => _offer is { IsClosing: false } && _stake > 0 && (Row is not null || _noGame);

    /// <summary>
    /// A closing price can be banked — the card carries the same block Log does — but never a
    /// $0 stake: the slip refuses one, so the key does not offer it.
    /// </summary>
    private bool CanBank => _offer is not null && _stake > 0 && Row is not null;

    /// <summary>
    /// What the Filled key is waiting for, said beside it. A closing line is not said here: it is
    /// the status note under the steps card, and a reason said twice is one too many. A missing
    /// stake still is, closing line or not: with no stake Add to slip is out of sight, and this
    /// line is what says the slip is a stake away.
    /// </summary>
    private string? Waiting
        => _offer is null && Typing
            ? (_noGame && string.IsNullOrWhiteSpace(_described)
                ? "Needs a description of the bet."
                : _typed.Waiting(_market, HasLine))
           : _stake <= 0 && !BuildParlay ? "Needs a stake above zero."
           : _offer is { IsClosing: true } ? null
           : null;

    /// <summary>
    /// A leg is a price the market still offers, or one the operator typed: never a close, which
    /// could not have been taken. No stake — the parlay holds the money.
    /// </summary>
    private bool CanBankLeg => _offer is { IsClosing: false } && Row is not null;

    private int LegCount => Slip.Mode == SlipMode.Parlay ? Slip.Legs.Count : Slip.Items.Count(i => i.AsLeg);

    /// <summary>What a leg key does, on the key only while it can be pressed.</summary>
    private const string LegTitle = "Bank this bet on the slip as a parlay leg, with no stake of its own";

    /// <summary>
    /// The sentence on the key row. Building a parlay, Add leg waits for the bet when there is
    /// none yet: that used to be its title, which a disabled key never shows. A closing line is
    /// still the status note's to say.
    /// </summary>
    private string? KeyReason
        => !BuildParlay || CanBankLeg ? Waiting
           : Waiting ?? (_offer is null ? "The bet and its price come first." : null);

    private IReadOnlyList<Check> Strip
    {
        get
        {
            if (_offer is not { } offer)
                return [];

            if (Row is null)
                return [Checks.Value(offer, _stake), Checks.Typed(null, DateTimeOffset.UtcNow)];

            return Checks.Build(offer, Row.Game.StartsAt, DateTimeOffset.UtcNow, _priorRead, _prior, _stake,
                new CheckActions(PullLabel, _pulling ? null : (Func<Task>)PullAsync, OpenJournalAsync),
                _priorFailed);
        }
    }

    /// <summary>"Pull NFL lines · 2 credits": the same job the Board row's pull runs, at the cost the menu quotes.</summary>
    private string? PullLabel
    {
        get
        {
            if (_linesJob is not { Available: true } job || Row?.Game.Sport is not { } sport)
                return null;

            var league = sport.Key.ToUpperInvariant();
            return job.EstimatedCredits > 0 ? $"Pull {league} lines · {job.EstimatedCredits} credits" : $"Pull {league} lines";
        }
    }

    private async Task PullAsync()
    {
        if (_linesJob is not { Available: true } job || _pulling)
            return;

        _pulling = true;

        try
        {
            var outcome = await Data.RunJobAsync(job.Key);

            if (outcome.Succeeded)
                Toasts.Success(outcome.Detail ?? $"{job.Label}: {outcome.Rows:N0} rows.");
            else
                Toasts.Warn(outcome.Detail ?? $"{job.Label}: {outcome.Failures} source failed.");

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "{Job} failed", job.Label);
            Toasts.Fail($"{job.Label} did not finish. Try again.");
        }
        finally
        {
            _pulling = false;
        }
    }

    private Task OpenJournalAsync()
    {
        Manager.Open(WindowCatalog.Find(WindowCatalog.Journal)!);
        return Task.CompletedTask;
    }

    private BetChoice Chosen()
        => new(_market, _outcome, _book, _manual, _typed.Price, _typed.Line, _typed.BookKey, _described, _sideChosen);

    /// <summary>
    /// What a successful Log does next (it was the receipt's New bet key). Everything the logged
    /// bet chose goes — the side, a typed price or line, the described bet, the stake and the
    /// note — and step 1 opens on the same game. The book stays as the default, since the next
    /// bet is usually at the same book; choosing a side shows its price there or the best one,
    /// as the first bet did. The receipt line is the caller's to set.
    /// </summary>
    private async Task NewBetAsync()
    {
        _logged = null;
        _error = null;
        _market = null;
        _outcome = null;
        _manual = false;
        _typed.Clear();
        _described = null;
        _stake = 0;
        _note = null;
        _moved = null;
        _opened = null;
        _sideChosen = false;
        _fromClose = false;
        _keptClose = false;
        _banked = null;

        Reprice();
        SyncUnsaved();
        await ReadPriorAsync();
    }

    /// <summary>
    /// The receipt line's first key: the side, the book and any typed price the logged bet had,
    /// put back on the reset wizard, and a fresh stake — a second stake on the same side is the
    /// one press. The duplicate check then names the bet just logged.
    /// </summary>
    private async Task SameBetAgainAsync()
    {
        if (_receipt is not { } receipt)
            return;

        var choice = receipt.Choice;
        _receipt = null;
        _logged = null;
        _opened = null;
        _market = choice.Market;
        _outcome = choice.Outcome;
        _book = choice.Book;
        _manual = choice.Manual;
        _typed.Restore(choice.TypedPrice, choice.TypedLine, choice.TypedBook);
        _described = choice.Described;
        _sideChosen = choice.SideChosen;

        Reprice();
        SyncUnsaved();
        await ReadPriorAsync();
    }

    /// <summary>
    /// Banks the wager on the slip, with its stake: the slip never takes a $0 bet. A closing
    /// price is allowed: the card carries the same block the Log key does here.
    /// </summary>
    /// <remarks>
    /// The window is left exactly as it was — side, price, book, stake and note — and stays
    /// marked as holding input. It used to clear the stake and mark itself clean, so the stake
    /// step folded empty, the bet looked lost, and the clean window could be evicted and reopen
    /// with nothing in it. The review's "On the slip" tag is the acknowledgment beside the bet;
    /// a press after a change banks the changed bet as a second card.
    /// </remarks>
    private void AddToSlip()
    {
        if (!CanBank || _offer is not { } offer || Row is null)
            return;

        // The duplicate check's reading travels with the card, so the slip can say it too.
        var card = Slip.Add(offer.ToSlipItem(Row.Game, Matchup(Row.Game), _stake, _note) with
        {
            InJournal = _priorRead && _prior is { } prior ? Checks.Prior(prior) : null
        });

        _error = null;
        _logged = $"Added {OddsMath.Money(_stake)} on {offer.Label} at {Price(offer.PriceAmerican)} to the slip.";
        _banked = new Banking(card.Id, ShapeNow!);
        Banked(_logged);
        SyncUnsaved();
    }

    /// <summary>The empty Slip tab's key: back to this window's own form.</summary>
    private void PickBet() => _tab = WagerTab.Wager;

    /// <summary>
    /// An addition to the slip, said as one: a green toast, with a key that turns this window
    /// to its Slip tab. The key runs outside a render, so it asks for one.
    /// </summary>
    private void Banked(string message)
        => Toasts.Added(message, "View slip", () => _ = InvokeAsync(() =>
        {
            _tab = WagerTab.Slip;
            StateHasChanged();
        }));

    /// <summary>
    /// Banks the bet as a parlay leg. On the game search the game is cleared afterwards, so the
    /// next leg starts from the search: a parlay is several games, found one after another.
    /// Anywhere else the bet stays on screen as it was, as Add to slip leaves it.
    /// </summary>
    private async Task AddLegAsync()
    {
        if (!CanBankLeg || _offer is not { } offer || Row is null)
            return;

        var card = Slip.AddLeg(offer.ToSlipItem(Row.Game, Matchup(Row.Game), 0m, _note) with
        {
            InJournal = _priorRead && _prior is { } prior ? Checks.Prior(prior) : null
        });

        var logged = $"Added {offer.Label} at {Price(offer.PriceAmerican)} ({offer.BookName}) as a leg · "
                     + $"{LegCount} {(LegCount == 1 ? "leg" : "legs")} on the slip.";
        Banked(logged);
        _banked = new Banking(card.Id, ShapeNow!);

        if (BuildParlay && Searching)
        {
            _note = null;
            await PickGameAsync(null);
        }

        _error = null;
        _logged = logged;
    }

    private async Task PlaceAsync()
    {
        if (!CanLog || _offer is not { } offer)
            return;

        _saving = true;
        _error = null;

        try
        {
            await Data.LogStraightsAsync([offer.ToDraft(Row?.Game.Id, _stake, _note)]);

            // The bet on screen may be the one banked on the slip a moment ago, still exactly as
            // banked. Logged here, it has gone to the journal, so its card leaves the slip:
            // otherwise the slip's Log would record the same bet a second time.
            if (_banked is { } banked && OnSlip)
                Slip.Logged([banked.Card]);

            // Hosted as a window there is no host to close the form, so it resets itself for the
            // next bet on the same game (a form left exactly as it was, key still lit, logged the
            // same wager twice on the next press) and says what it did in one line above the
            // steps, with Same bet again for a second stake on this side.
            var receipt = new LoggedWager(offer.Label, offer.PriceAmerican, offer.BookName, _stake, offer.Manual, Chosen());
            var logged = $"Logged {OddsMath.Money(_stake)} on {offer.Label} at {Price(offer.PriceAmerican)} ({offer.BookName})"
                         + (offer.Manual ? ", your own price." : ".");
            Toasts.Success(logged);

            // The reset re-reads the journal for a side that is no longer chosen, which clears the check.
            await NewBetAsync();
            _receipt = receipt;
            _logged = logged;

            await OnPlaced.InvokeAsync();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The wager was not logged");
            _error = "The entry was not logged. Try again.";
        }
        finally
        {
            _saving = false;
        }
    }
}
