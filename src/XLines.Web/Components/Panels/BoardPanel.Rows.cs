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

public partial class BoardPanel
{
    /// <summary>
    /// Opening a row is what reveals its actions. One at a time: two open rows would push the
    /// board around and the actions would stop being obviously attached to a game.
    /// </summary>
    private void Toggle(BoardRow row)
    {
        _open = _open?.Game.Id == row.Game.Id ? null : row;
        _snippet = null;
        _take = null;
        _watched = null;

        // Line watch (ADR 0029): a game opened here is the game Line movement shows. The row
        // still opens, so its strip — and the take behind Place wager — is a press away.
        if (_open is not null && Watching)
            Watch(row, market: null, outcome: null);
    }

    /// <summary>
    /// The open row, if the rows now showing still include its game; otherwise none. By game,
    /// because a reload brings the same game back as a new row.
    /// </summary>
    public static BoardRow? StillOpen(BoardRow? open, IReadOnlyList<BoardRow> visible)
        => open is not null && visible.FirstOrDefault(r => r.Game.Id == open.Game.Id) is { } row ? row : null;

    /// <summary>
    /// A strip handing over its snippet, or taking it back. Only the open game's strip is heard:
    /// a strip going away after another row opened cannot clear the newer row's snippet.
    /// </summary>
    private void HostSnippet(int gameId, RenderFragment? snippet)
    {
        if (_open?.Game.Id != gameId)
            return;

        // A take owns the row's snippet until something replaces it: the strip it re-keyed
        // away hands back null on its way out, and that must not put the take away.
        if (snippet is null && _take is not null)
            return;

        _take = null;
        _snippet = snippet;
    }

    /// <summary>
    /// Presses the pull menu rather than running a job directly, so an inline offer and the
    /// menu entry are the same press: same busy key, same result note, same reload.
    /// </summary>
    private async Task PullAsync(string key)
    {
        if (_pull is null || !_pullingKeys.Add(key))
            return;

        try
        {
            await _pull.RunAsync(key);
        }
        finally
        {
            _pullingKeys.Remove(key);
        }
    }

    /// <summary>
    /// A pull runs in the menu or on the Date range key; the panel only reflects it on its own
    /// pulse strip. Counted, not flagged: the two keys can run at once, and the first to finish
    /// must not read as the end of both.
    /// </summary>
    private void OnPullBusy(bool busy)
    {
        _pullsRunning = Math.Max(0, _pullsRunning + (busy ? 1 : -1));

        if (_pullsRunning > 0)
            Report(PulseState.Active, "pulling");
    }

    /// <summary>
    /// What this row can do, in the order an operator reaches for them.
    ///
    /// <para>
    /// The first three answer their question on the row — a price, a fixture's history, both
    /// sides' form — because none of them is worth evicting a window for. So does More bets:
    /// the book-by-book table opens under the row, and its escape makes it a window or a tab.
    /// Place wager is a command: it opens the take in the row.
    /// </para>
    ///
    /// <para>
    /// A row with no price on it offers neither: there is nothing to lay out book by book and
    /// nothing to wager against. It gets the pull that would fix it instead, where the gap is
    /// stated — and only while a pull could still help, since no book will re-quote a game that
    /// has already started.
    /// </para>
    /// </summary>
    private List<RowAction> ActionsFor(BoardRow row)
    {
        var id = row.Game.Id;
        var name = Matchup(row.Game);

        var actions = new List<RowAction>
        {
            new()
            {
                Key = "odds",
                Label = "Odds",
                Icon = DeskIcons.ShowChart,
                Snippet = OddsGlance(id),
                OpenLabel = "Movement",
                OpenWindow = Command(() => Launch(WindowCatalog.Odds, "GameId", id, name))
            },
            new()
            {
                Key = "h2h",
                Label = "H2H",
                Icon = DeskIcons.CompareArrows,
                Snippet = HeadToHeadGlance(id),
                OpenLabel = "Full history",
                OpenWindow = Command(() => Launch(WindowCatalog.HeadToHead, "GameId", id, name))
            },
            new()
            {
                Key = "form",
                Label = "Form",
                Icon = DeskIcons.QueryStats,
                Snippet = FormGlance(id),
                OpenLabel = row.Bout is null ? "Both rosters" : "Tale of the tape",
                OpenWindow = Command(() => Launch(WindowCatalog.Form, "GameId", id, name))
            }
        };

        // A live NFL game can be followed play by play. A destination, not a glance: the drive
        // log and the field are somewhere to watch from for a whole half, so it opens a desk
        // window, as the matchup's own link opens the Game window, rather than a follow-up dialog.
        if (row.Game.Status == GameStatus.Live && GamecastPanel.Offered(row.Game))
        {
            actions.Add(new RowAction
            {
                Key = "gamecast",
                Label = "Gamecast",
                Icon = DeskIcons.SportsFootball,
                OpenWindow = Command(() => Manager.OpenGamecast(id, WindowShortcuts.Describe(row.Game)))
            });
        }

        // A visitor reads the board — every book's number — but has no wager to log and no
        // lines to pull (ADR 0018).
        var operate = Manager.Access.CanOperate;

        if (row.HasPrices)
        {
            // Every book's number drops down under the row, as the glances do. It was a
            // destination, and on a full desk a destination floats: the dialog sat over the
            // slate it was opened from, the open row and its take behind it. The table is three
            // short grids, which the row has the width for; the escape is for keeping it.
            actions.Add(new RowAction
            {
                Key = "bets",
                Label = "More bets",
                Icon = DeskIcons.ViewList,
                Snippet = AllBetsGlance(id),
                // Its markets are section cards of their own, so the drawer is their pane header.
                SnippetCards = true,
                OpenLabel = Manager.AtCapacity ? "Open as tab" : "Open window",
                OpenWindow = Command(() => OpenBets(id, name))
            });

            // Place wager takes the wager key's own words, in the markup beside the glances (PlaceWagerAction).
            if (operate)
                actions.Add(PlaceWagerAction(row));
        }
        else if (operate && CanPull(row))
        {
            actions.Add(new RowAction
            {
                Key = "pull",
                // The pull runs on the toolbar's menu, and the row's key says so too: busy, and
                // worded as what is happening, so a press here is answered here rather than only
                // by a key at the top of the window. This sport's lines, not any pull: a range
                // pull for another league leaves this key as it was.
                Label = _pullingKeys.Contains(IngestionJobs.OddsLinesPrefix + row.Game.Sport!.Key)
                    ? $"Pulling {row.Game.Sport!.Key.ToUpperInvariant()} lines"
                    : $"Pull {row.Game.Sport!.Key.ToUpperInvariant()} lines",
                Icon = DeskIcons.CloudDownload,
                Emphasis = DeskEmphasis.Tinted,
                Busy = _pullingKeys.Contains(IngestionJobs.OddsLinesPrefix + row.Game.Sport!.Key),
                OpenWindow = Command(() => PullAsync(IngestionJobs.OddsLinesPrefix + row.Game.Sport!.Key))
            });
        }

        return actions;
    }

    /// <summary>
    /// A row command as a callback this panel owns, so the panel is drawn after it. The press is
    /// the strip's event; run as a bare delegate from there, a command that changed this panel's
    /// state — the take opened under the row, a Pull key marked busy — drew nothing until a
    /// refresh or a live score redrew the board.
    /// </summary>
    private EventCallback Command(Func<Task> run) => EventCallback.Factory.Create(this, run);

    private EventCallback Command(Action run) => EventCallback.Factory.Create(this, run);

    /// <summary>
    /// Whether pulling lines could still fill this row. No book re-quotes a game that is under
    /// way, so offering the pull there would be a key that cannot work.
    /// </summary>
    private static bool CanPull(BoardRow row)
        => row.Game.Sport is not null
           && row.Game.Status == GameStatus.Scheduled
           && row.Game.StartsAt > DateTimeOffset.UtcNow;

    /// <summary>
    /// Every book as a window of its own, never a dialog over the Board: a column while one is
    /// free, and once none is the front tab of the column beside the Board (WindowManager.Open
    /// keeps out of the column being worked in) — or of the Board's own on a one-column desk.
    /// </summary>
    private void OpenBets(int id, string name)
    {
        if (Manager.Catalog.Find(WindowCatalog.Bets) is not { } definition)
            return;

        Manager.Open(definition, new Dictionary<string, object> { ["GameId"] = id }, $"{definition.Title} · {name}");
    }

    private Task Launch(string key, string parameter, int id, string name, string width = "640px")
    {
        _followUps.Launch(Manager, key, parameter, id, name, width);
        return Task.CompletedTask;
    }

    /// <summary>A price taken in its row: which game, which side, from which book.</summary>
    private sealed record TakeRequest(int GameId, string Market, string Outcome, string? Book);

    /// <summary>The take open under the open row, or null. It owns the row's snippet while it is.</summary>
    private TakeRequest? _take;

    /// <summary>
    /// In Line watch, the price last pressed on the open row: shown in Line movement, and the one its
    /// Place wager takes. Cleared when the row closes or another row opens.
    /// </summary>
    private TakeRequest? _watched;

    /// <summary>
    /// The row's Place wager: the take on the price watched on this row, else on the most value,
    /// else — nothing to start from — the full form.
    /// </summary>
    private Task PlaceWager(BoardRow row)
    {
        if (_watched is { } watched && watched.GameId == row.Game.Id)
            return OpenTake(row, watched.Market, watched.Outcome, watched.Book);

        if (row.BestPick is { } pick)
            return OpenTake(row, pick.Market, pick.Outcome, pick.Book.Book);

        return Launch(WindowCatalog.Wager, "GameId", row.Game.Id, Matchup(row.Game), "480px");
    }

    /// <summary>
    /// Bumped each time a take opens, and part of the row strip's key, so the strip is redrawn with
    /// nothing chosen rather than lighting the segment of a glance the take has replaced.
    /// </summary>
    private int _takeSerial;

    /// <summary>How many pulls are running from the toolbar's two keys; the pulse strip is Active while any is.</summary>
    private int _pullsRunning;

    /// <summary>The job keys pressed from a row that are still running; that row's Pull lines key shows it.</summary>
    private readonly HashSet<string> _pullingKeys = [];

    private bool IsTaking(BoardRow row, MarketPair pair, BestOffer? offer)
        => offer is not null && _take is { } take && take.GameId == row.Game.Id
           && take.Market == pair.Market && take.Outcome == offer.Outcome;

    /// <summary>Whether the slip holds this cell's price: the same side, line and book.</summary>
    private bool IsBanked(BoardRow row, MarketPair pair, BestOffer? offer)
        => offer is not null && Slip.Items.Any(i => i.GameId == row.Game.Id && i.Market == pair.Market
               && i.Outcome == offer.Outcome && i.Line == offer.Line
               && string.Equals(i.Book, offer.Book, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Door 2: a live price opens its take under the row — the stake, what is worth knowing and
    /// Log, without a window over the board. The same price pressed again puts it away. The full
    /// wager form is the take's escape, for what a row cannot say.
    /// </summary>
    private Task Take(BoardRow row, MarketPair pair, BestOffer? offer)
    {
        if (offer is null)
            return Task.CompletedTask;

        // Line watch (ADR 0029): a price pressed shows its market and side in Line movement, and
        // the row opens on it with nothing taken — the take is the open row's Place wager key.
        // A take already open, on this row or another, is put away: left open it would go on
        // offering, and logging, the price pressed before.
        if (Watching)
        {
            if (_open?.Game.Id != row.Game.Id)
            {
                _open = row;
                _snippet = null;
                _take = null;
            }
            else if (_take is not null)
                CloseTake();

            // The price the row's Place wager takes, until the row closes or another opens.
            _watched = new TakeRequest(row.Game.Id, pair.Market, offer.Outcome, offer.Book);
            Watch(row, pair.Market, offer.Outcome);
            return Task.CompletedTask;
        }

        if (IsTaking(row, pair, offer))
        {
            CloseTake();
            return Task.CompletedTask;
        }

        return OpenTake(row, pair.Market, offer.Outcome, offer.Book);
    }

    /// <summary>
    /// Opens the row, if it is not, and draws the take as its snippet. Reached from a price cell's
    /// own press and from the row's Place wager key (a <see cref="Command"/>), and this panel is
    /// drawn after either.
    /// </summary>
    private Task OpenTake(BoardRow row, string market, string outcome, string? book)
    {
        _open = row;
        _take = new TakeRequest(row.Game.Id, market, outcome, book);
        _takeSerial++;
        _snippet = TakeSnippet;
        return Task.CompletedTask;
    }

    private void CloseTake()
    {
        _take = null;
        _snippet = null;
    }

    /// <summary>
    /// Another side chosen in the open take (its bet pop-up): the request follows, so the cell
    /// lit on the row and the form the take's escape opens are that side's, on its best book.
    /// In Line watch it is also the side being watched, as a pressed price is (ADR 0029).
    /// </summary>
    private void ChooseSide(BoardRow row, string market, string outcome)
    {
        if (_take is not { } take || take.GameId != row.Game.Id)
            return;

        _take = take with { Market = market, Outcome = outcome, Book = null };

        if (Watching)
        {
            _watched = _take;
            Watch(row, market, outcome);
        }
    }

    /// <summary>
    /// The take's escape: the full wager form on the same side and book, with the stake typed so
    /// far. With <paramref name="typed"/>, the form opens with its price typed from that number,
    /// through the same parameters a copied bet's typed price takes (WagerPrefill.ToParameters).
    /// </summary>
    private Task OpenForm(BoardRow row, TakeRequest take, string book, decimal stake, TypedTake? typed = null)
    {
        var parameters = TypedTake.Door(row.Game.Id, take.Market, take.Outcome,
            book.Length > 0 ? book : take.Book ?? string.Empty, stake, typed);

        _followUps.Launch(Manager, WindowCatalog.Wager, parameters, row.Game.Id, Matchup(row.Game), "480px");
        CloseTake();
        return Task.CompletedTask;
    }

    private void ViewSlip() => _ = InvokeAsync(() => Manager.Open(WindowCatalog.Find(WindowCatalog.Slip)!));

    /// <summary>
    /// Door 2, shifted: a live price goes onto the slip as a parlay leg, at the book the cell
    /// shows, with no window opened. The toast is the acknowledgment and names what the slip now
    /// holds, as the wager window's badge does.
    /// </summary>
    private Task Bank(BoardRow row, MarketPair pair, BestOffer? offer)
    {
        if (offer is null || offer.IsClosing)
            return Task.CompletedTask;

        var price = XLines.Web.Services.WagerOffer.Best(pair.Market, offer);
        Slip.AddLeg(price.ToSlipItem(row.Game, Matchup(row.Game), 0m, null));

        var legs = Slip.Items.Count(i => i.AsLeg);
        Toasts.Added($"Added a leg: {price.Label} {Price(price.PriceAmerican)} ({price.Book}) · "
                     + $"{legs} {(legs == 1 ? "leg" : "legs")} on the slip.",
            "View slip", () => _ = InvokeAsync(() => Manager.Open(WindowCatalog.Find(WindowCatalog.Slip)!)));

        return Task.CompletedTask;
    }
}
