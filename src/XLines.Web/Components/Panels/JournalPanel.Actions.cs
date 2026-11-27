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

// The Journal's keys: the toolbar's doors to the wager window, a row's Duplicate, Delete and
// Settle, settlement, and a selection's link to its game.
public partial class JournalPanel
{
    /// <summary>
    /// Opens the wager window on its game search: the one form a new bet is recorded with. The
    /// last result line goes: a stale "Entry saved." beside a new bet describes some earlier minute.
    /// </summary>
    private void OpenWager()
    {
        _message = null;
        Manager.Open(WindowCatalog.Find(WindowCatalog.Wager)!);
    }

    /// <summary>
    /// The same wager window, building a parlay: each bet picked there is a leg on the slip, and
    /// the slip turns to parlay mode so the legs read as one bet. Logged from the Slip tab, here
    /// or in that window, once it is whole.
    /// </summary>
    private void BuildParlay()
    {
        _message = null;
        Slip.SetMode(SlipMode.Parlay);
        Manager.Open(WindowCatalog.Find(WindowCatalog.Wager)!,
            new Dictionary<string, object> { ["BuildParlay"] = true }, "Build parlay");
    }

    /// <summary>A new wager window on this bet: the shared door, so it means what the wager window's own Duplicate means.</summary>
    private void Duplicate(JournalEntry entry)
    {
        _message = null;
        Manager.OpenWagerCopy(WagerPrefill.From(entry), entry.Game is { } game ? Matchup(game) : null);
    }

    /// <summary>
    /// Puts a logged parlay back on the slip to log again: each leg at the price it was taken at,
    /// typed rather than fetched (<see cref="SlipItem.ManualPrice"/>), so a leg on a game that
    /// has started is a leg and not a closing line; then the parlay's stake, book and price. The
    /// slip is shown in its tab here, beside the journal it was copied from.
    ///
    /// <para>
    /// The slip holds one parlay, so wagers already on it are asked about before they are
    /// replaced: they are banked and unlogged, and a copy laid over them would mix the two.
    /// </para>
    /// </summary>
    private async Task DuplicateParlayAsync(Parlay parlay)
    {
        _message = null;

        // The slip holds bets on games the desk tracks, at a price. A leg that is a described
        // bet, on no tracked game, or logged without its price has no card to become.
        if (parlay.Legs.Any(l => l.Game is null || !string.IsNullOrWhiteSpace(l.FreeTextMarket) || l.PriceTaken == 0))
        {
            _message = "The slip holds bets on tracked games at a price, and a leg of this parlay is not one. Nothing was copied.";
            return;
        }

        if (Slip.Items.Count > 0)
        {
            var held = Slip.Items.Count == 1 ? "1 wager" : $"{Slip.Items.Count} wagers";
            var replace = await Alerts.ConfirmAsync(
                "Replace the slip?",
                confirmLabel: "Replace",
                message: $"The slip holds {held}. They leave it unlogged, and this parlay's {parlay.Legs.Count} legs take their place.",
                destructive: true);

            if (!replace)
                return;

            Slip.Clear();
        }

        foreach (var leg in parlay.Legs.OrderBy(l => l.Id))
        {
            var game = leg.Game!;
            Slip.AddLeg(new SlipItem(
                Guid.Empty,
                game.Id, Matchup(game), game.StartsAt, game.Sport?.Key ?? string.Empty,
                leg.Market, leg.Outcome, leg.LineTaken,
                parlay.Book, leg.PriceTaken, leg.PlacedAt, IsClosing: false,
                Stake: 0m, Note: null, Fair: null, Ev: null,
                ManualPrice: true)
            {
                OutcomeAbbrev = SlipItem.ClubAbbrev(game, leg.Outcome),
            });
        }

        Slip.SetMode(SlipMode.Parlay);
        Slip.SetParlay(parlay.Stake, parlay.PriceQuoted, parlay.Book);

        _tab = JournalTab.Slip;
        // A toast, not the toolbar's line every other Journal action reports in: the window turns
        // to the Slip tab, where that line is not drawn.
        Toasts.Success($"Copied the {parlay.Legs.Count}-leg parlay to the slip");
    }

    private async Task DeleteEntryAsync(JournalEntry entry)
    {
        var confirmed = await Alerts.ConfirmAsync(
            "Delete this entry?",
            message: $"{Describe(entry)} — {OddsMath.Money(entry.Stake)} at {Price(entry.PriceTaken)}. It leaves the journal and every figure built on it.",
            confirmLabel: "Delete", destructive: true);

        if (!confirmed)
            return;

        try
        {
            await Data.DeleteEntryAsync(entry.Id);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Journal entry {Id} was not deleted", entry.Id);
            _message = "The entry was not deleted. Try again.";
            return;
        }

        _message = "Entry deleted.";
        await LoadAsync();
    }

    private async Task DeleteParlayAsync(Parlay parlay)
    {
        var confirmed = await Alerts.ConfirmAsync(
            "Delete this parlay?",
            message: $"{parlay.Legs.Count} legs, {OddsMath.Money(parlay.Stake)}. The parlay and all its legs leave the journal.",
            confirmLabel: "Delete", destructive: true);

        if (!confirmed)
            return;

        try
        {
            await Data.DeleteParlayAsync(parlay.Id);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Parlay {Id} was not deleted", parlay.Id);
            _message = "The parlay was not deleted. Try again.";
            return;
        }

        _message = "Parlay deleted.";
        await LoadAsync();
    }

    private async Task SetResultAsync(int entryId, EntryResult result)
    {
        try
        {
            await Data.SetResultAsync(entryId, result);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The result on journal entry {Id} was not recorded", entryId);
            _message = "The result was not recorded. Try again.";
            return;
        }

        await LoadAsync();
    }

    private async Task SetParlayResultAsync(Parlay parlay, EntryResult result)
    {
        var payout = result == EntryResult.Win && parlay.PriceQuoted is { } q
            ? Math.Round(OddsMath.PayoutOnWin(q, parlay.Stake), 2, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        try
        {
            await Data.SetParlayResultAsync(parlay.Id, result, payout);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The result on parlay {Id} was not recorded", parlay.Id);
            _message = "The result was not recorded. Try again.";
            return;
        }

        await LoadAsync();
    }

    private async Task SettleAsync()
    {
        _busy = true;
        Report(PulseState.Active, "settling");

        try
        {
            var summary = await Data.SettleAsync();

            // A sentence, not counts strung on middle dots, which read as a row of tags.
            _message = $"{summary.Graded} graded, {summary.Voided} voided, {summary.ParlaysSettled} "
                       + $"{(summary.ParlaysSettled == 1 ? "parlay" : "parlays")} settled, {summary.ClvResolved} CLV resolved, "
                       + $"{summary.LeftPending} still pending.";

            // The reload catches its own failure (WithActivityAsync), so a catch here is the
            // settlement's alone.
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Settlement did not run");
            _message = "Settlement did not run. Try again.";
            Report(PulseState.Critical, "settle failed");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task OpenGameAsync(int gameId)
    {
        var game = await Data.GameAsync(gameId);

        if (game is not null)
            Manager.OpenGame(game);
    }
}
