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

// The Journal's two edit forms: a straight bet and a logged parlay, the drafts they hold, the
// rules a field is held to, and the funnel every change comes through.
public partial class JournalPanel
{
    /// <summary>
    /// A logged parlay as the edit form holds it: the money and the book, which are the parlay's,
    /// and the price on each leg, by the leg's id — null where the field is empty, as it opens on
    /// a leg logged without one.
    /// </summary>
    private sealed class ParlayEditDraft(Parlay parlay)
    {
        public Parlay Parlay { get; } = parlay;
        public string Book { get; set; } = parlay.Book;
        public decimal Stake { get; set; } = parlay.Stake;
        public decimal? PriceQuoted { get; set; } = parlay.PriceQuoted;
        public string? Note { get; set; } = parlay.Note;

        public Dictionary<int, decimal?> LegPrices { get; } =
            parlay.Legs.ToDictionary(l => l.Id, l => l.PriceTaken == 0 ? (decimal?)null : l.PriceTaken);

        /// <summary>
        /// An empty leg field sends nothing: the leg keeps the price it has, or its lack of one.
        /// Each price goes as the American price it is read as, so decimal odds typed here are
        /// stored as the wager window stores them.
        /// </summary>
        public ParlayEdit ToEdit() => new(
            Book, Stake, PriceQuoted is { } q ? ReadPrice(q) ?? q : null, Note,
            LegPrices.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => ReadPrice(p.Value) ?? p.Value!.Value));
    }

    /// <summary>
    /// A straight bet as the edit form holds it. New bets are logged in the wager window; parlays
    /// are built on the slip.
    /// </summary>
    private sealed class Draft
    {
        /// <summary>The price as logged, so an edit to it can be told from an edit to the stake.</summary>
        public decimal OriginalPrice { get; init; }
        public decimal? OriginalLine { get; init; }
        public bool WasManual { get; init; }

        /// <summary>
        /// A typed price stays typed; a fetched one becomes typed once its number is edited here,
        /// because the number in the journal is then the operator's, not the desk's.
        /// </summary>
        public bool ManualPrice => WasManual || LoggedPrice != OriginalPrice || LineTaken != OriginalLine;

        /// <summary>The price as it is logged: the typed one, read as American odds where it is decimal.</summary>
        public decimal LoggedPrice => ReadPrice(PriceTaken) ?? PriceTaken;

        public int? GameId { get; set; }
        public Game? Game { get; set; }
        public string Market { get; set; } = Markets.Spread;
        public string? FreeTextMarket { get; set; }
        public string Outcome { get; set; } = string.Empty;
        public decimal? LineTaken { get; set; }
        public decimal PriceTaken { get; set; } = -110;
        public decimal Stake { get; set; } = 100m;
        public string Book { get; set; } = string.Empty;
        public string? Note { get; set; }

        public EntryDraft ToDraft() => new(
            GameId, Market == FreeText ? "other" : Market,
            Market == FreeText ? FreeTextMarket : null,
            Outcome, LineTaken, LoggedPrice, Stake, Book, Note, ManualPrice);
    }

    private static bool EntryValid(Draft d, bool needsPrice)
        => StakeError(d.Stake) is null
           && (!needsPrice || PriceError(d.PriceTaken) is null)
           && !string.IsNullOrWhiteSpace(d.Book)
           && (d.Market == FreeText ? !string.IsNullOrWhiteSpace(d.FreeTextMarket) : !string.IsNullOrWhiteSpace(d.Outcome));

    /// <summary>
    /// A typed price is American odds to the cent or decimal odds, read as the wager window and
    /// the slip read them (<see cref="OddsMath.ReadTypedPrice"/>): 2.0435 is +104.35. What is
    /// neither — below 1.01 and above −100 — is a typo, most often a dropped digit, and zero is
    /// no price at all.
    /// </summary>
    public static string? PriceError(decimal price)
        => OddsMath.ReadTypedPrice(price) is null ? "A price is −100 or lower, +100 or higher, or decimal odds with a fraction, such as 2.05." : null;

    /// <summary>The American price a typed price is read as; null where it is none, or nothing is typed.</summary>
    public static decimal? ReadPrice(decimal? typed)
        => typed is { } t ? OddsMath.ReadTypedPrice(t) : null;

    /// <summary>
    /// What a typed decimal price is read as — "Decimal 2.0435 = +104.35" — so the number logged
    /// is on screen first, as the slip says it. Null for an American price, or nothing typed.
    /// </summary>
    public static string? DecimalEcho(decimal? typed)
        => XLines.Web.Components.Desk.TypedPrice.EchoOf(typed);

    public static string? StakeError(decimal stake)
        => stake <= 0 ? "Enter a stake above zero." : null;

    /// <summary>
    /// A spread or a total with no number cannot be graded. A total of zero or less is not a
    /// line any game is offered at — the message says "above zero", so zero is held to it too.
    /// A spread can sit at zero (a pick'em) or either side of it.
    /// </summary>
    public static string? LineError(string market, decimal? line) => market switch
    {
        Markets.Spread or Markets.Total when line is null => "Enter the line.",
        Markets.Total when line <= 0 => "A total is above zero.",
        _ => null
    };

    /// <summary>
    /// Whether the form holds anything the journal does not. A note or a described bet cleared
    /// back to nothing is the null it was opened with, and the manual-price flag follows the
    /// price and line, so neither counts as a change of its own.
    /// </summary>
    public static bool EntryIsDirty(EntryDraft current, EntryDraft opened)
    {
        static EntryDraft Plain(EntryDraft d) => d with
        {
            Note = string.IsNullOrWhiteSpace(d.Note) ? null : d.Note,
            FreeTextMarket = string.IsNullOrWhiteSpace(d.FreeTextMarket) ? null : d.FreeTextMarket,
            ManualPrice = false
        };

        return Plain(current) != Plain(opened);
    }

    /// <summary>
    /// Whether the parlay form holds anything the journal does not, by the rule the entry form
    /// keeps: a book differing only in case or spaces is the same book once stored, a cleared
    /// note is the null it opened with, and the book's price of zero is no price.
    /// </summary>
    public static bool ParlayIsDirty(ParlayEdit current, ParlayEdit opened)
    {
        static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        static IEnumerable<KeyValuePair<int, decimal>> Prices(ParlayEdit e)
            => (e.LegPrices ?? new Dictionary<int, decimal>()).OrderBy(p => p.Key);

        return !string.Equals(Text(current.Book), Text(opened.Book), StringComparison.OrdinalIgnoreCase)
               || current.Stake != opened.Stake
               || (current.PriceQuoted is 0 ? null : current.PriceQuoted) != (opened.PriceQuoted is 0 ? null : opened.PriceQuoted)
               || Text(current.Note) != Text(opened.Note)
               || !Prices(current).SequenceEqual(Prices(opened));
    }

    /// <summary>
    /// A leg's price field: a typed number is held to what a price is, and an emptied field on a
    /// leg that had a price asks for it back — clearing it would leave the parlay a price short
    /// to reprice on, which is not a correction. A leg logged without one may stay without.
    /// </summary>
    private static string? LegPriceError(decimal? typed, decimal logged)
        => typed is { } price ? PriceError(price)
            : logged != 0 ? "Enter the price this leg was taken at."
            : null;

    /// <summary>
    /// What the parlay pays at, for the stake row's Returns: the book's price where it gave a
    /// real one, otherwise the legs' prices multiplied, as the slip reads it. None where a leg has
    /// no price, or for legs on one game, which win and lose together and do not multiply.
    /// </summary>
    private static decimal? CombinedPrice(ParlayEditDraft d)
    {
        if (d.PriceQuoted is { } quoted)
            return ReadPrice(quoted);

        var legs = d.Parlay.Legs;
        if (legs.Count < 2 || legs.Where(l => l.GameId is not null).GroupBy(l => l.GameId).Any(g => g.Count() > 1))
            return null;

        var prices = legs.Select(l => ReadPrice(d.LegPrices.GetValueOrDefault(l.Id))).ToList();
        return prices.All(p => p is not null)
            ? OddsMath.CombinedAmerican(prices.Select(p => p!.Value))
            : null;
    }

    /// <summary>What Save is waiting for, said beside it; null when the form can be saved.</summary>
    private static string? ParlayWaiting(ParlayEditDraft d)
        => StakeError(d.Stake) is not null ? "Needs a stake above zero."
            : string.IsNullOrWhiteSpace(d.Book) ? "Needs the book the parlay was placed at."
            : d.PriceQuoted is { } quoted && PriceError(quoted) is not null
                ? "The book's price is −100 or lower, +100 or higher, or decimal odds with a fraction, such as 2.05."
            : d.Parlay.Legs.Any(l => LegPriceError(d.LegPrices.GetValueOrDefault(l.Id), l.PriceTaken) is not null)
                ? "Each leg needs the price it was taken at: −100 or lower, +100 or higher, or decimal odds with a fraction, such as 2.05."
            : null;

    // A total's two sides as the grader reads them, lower-case.
    private static readonly IReadOnlyList<DeskSwitchOption<string>> TotalSides =
    [
        new("over", "Over"),
        new("under", "Under")
    ];

    // A field says what is wrong only once it has been touched: an error on a box nobody has
    // reached yet tells the operator they are wrong before they have said anything.
    private readonly HashSet<string> _touched = [];

    /// <summary>The entry as it was opened, so the form can tell an edit from a look.</summary>
    private EntryDraft? _opened;

    private bool Touched(string field) => _touched.Contains(field);

    /// <summary>
    /// Every field's change comes through here: it marks the field touched, and tells the window
    /// whether it now holds input the journal does not — so closing it, or evicting it, asks first.
    /// </summary>
    private void Edit(string field, Action change)
    {
        change();
        _touched.Add(field);

        if (_opened is not null)
            MarkUnsaved(EntryIsDirty(_entry.ToDraft(), _opened), "this journal entry");
        else if (_parlay is not null && _parlayOpened is not null)
            MarkUnsaved(ParlayIsDirty(_parlay.ToEdit(), _parlayOpened), "this parlay");
    }

    private void EditEntry(JournalEntry entry)
    {
        _message = null;
        _editing = entry;
        _entry = new Draft
        {
            GameId = entry.GameId,
            Game = entry.Game,
            Market = entry.FreeTextMarket is null ? entry.Market : FreeText,
            FreeTextMarket = entry.FreeTextMarket,
            Outcome = entry.Outcome == "—" ? string.Empty : entry.Outcome,
            // Without the column's scale, so the field reads "-3" as the book printed it and not
            // "-3.00". Decimal equality ignores scale, so this is not an unsaved change.
            LineTaken = entry.LineTaken is { } line ? line / 1.000000000000000000000000000000000m : null,
            PriceTaken = entry.PriceTaken,
            Stake = entry.Stake,
            Book = entry.Book,
            Note = entry.Note,
            OriginalPrice = entry.PriceTaken,
            OriginalLine = entry.LineTaken,
            WasManual = entry.ManualPrice
        };
        _opened = _entry.ToDraft();
        _parlay = null;
        _parlayOpened = null;
        _touched.Clear();
        _mode = FormMode.Entry;

        // Opened on another bet, the form holds nothing the journal lacks yet.
        MarkUnsaved(false);
    }

    /// <summary>The same grouped form, on a parlay: its money and book, and the price on each leg.</summary>
    private void EditParlay(Parlay parlay)
    {
        _message = null;
        _editing = null;
        _opened = null;
        _parlay = new ParlayEditDraft(parlay);
        _parlayOpened = _parlay.ToEdit();
        _touched.Clear();
        _mode = FormMode.EditParlay;
        MarkUnsaved(false);
    }

    private void CloseForm()
    {
        _mode = FormMode.None;
        _editing = null;
        _opened = null;
        _parlay = null;
        _parlayOpened = null;
        _touched.Clear();

        // Saved or cancelled, the form holds nothing the journal lacks.
        MarkUnsaved(false);
    }

    private Task SetParlayBookAsync(string book)
    {
        if (_parlay is { } edited)
            Edit("book", () => edited.Book = book);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves the parlay form. A write that fails keeps the form open with what was typed, and
    /// says so: the operator's corrections are not thrown away with the error.
    /// </summary>
    private async Task SaveParlayChangesAsync()
    {
        if (_parlay is not { } edited || ParlayWaiting(edited) is not null)
            return;

        try
        {
            await WriteParlayAsync(edited.Parlay.Id, edited.ToEdit());
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The parlay did not save");
            _message = "The parlay was not saved. Try again.";
            return;
        }

        _message = "Parlay saved.";
        CloseForm();
        await LoadAsync();
    }

    private Task SetEntryBookAsync(string book)
    {
        Edit("book", () => _entry.Book = book);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves the entry form. As the parlay form does, a write that fails keeps the form open with
    /// what was typed and says so in the toolbar, rather than taking the operator's work with it.
    /// </summary>
    private async Task SaveEntryAsync()
    {
        try
        {
            await WriteEntryAsync(_entry.ToDraft(), _editing?.Id);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Journal entry {Id} did not save", _editing?.Id);
            _message = "The entry was not saved. Try again.";
            return;
        }

        _message = _editing is null ? "Entry logged." : "Entry saved.";
        CloseForm();
        await LoadAsync();
    }
}
