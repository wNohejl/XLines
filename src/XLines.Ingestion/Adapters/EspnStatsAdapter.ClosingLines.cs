using System.Globalization;
using System.Text.Json;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Adapters;

/// <summary>
/// ESPN's closing lines, read: from a finished game's summary and from the odds endpoint,
/// a reference for games no market covered and never the market itself (ADR 0011).
/// </summary>
public partial class EspnStatsAdapter
{
    /// <summary>
    /// The line as it closed, from the odds block ESPN attaches to a finished game's summary.
    ///
    /// <para>
    /// This is deliberately narrow. ESPN carries one book, and one book is a price rather than a
    /// market — which is exactly why <c>ADR 0011</c> refuses it as the odds port. Nothing here
    /// changes that: these rows are only ever read for games that have already finished, where
    /// the question is "what did the market think of this matchup" rather than "what should I
    /// bet now". A real book market always takes precedence; this fills the gap for the
    /// thousands of past fixtures nobody was watching live.
    /// </para>
    ///
    /// <para>
    /// ESPN states <c>close</c> explicitly alongside <c>open</c>, so this is the closing number
    /// as the provider reports it rather than the last value we happened to observe — a better
    /// record than our own scan tier could produce for a game we never watched.
    /// </para>
    ///
    /// <para>
    /// Prices are read from the per-side <c>close</c> blocks rather than the flatter
    /// <c>homeTeamOdds</c>/<c>overOdds</c> fields, because those carry the moneyline and total
    /// price but not the spread's, and a spread stored at an assumed -110 would be a fabricated
    /// number sitting in the same column as measured ones.
    /// </para>
    /// </summary>
    internal static IEnumerable<CanonicalOdds> ParseClosingLines(
        JsonElement root, string sourceGameId, string homeTeam, string awayTeam, DateTimeOffset capturedAt)
    {
        if (!root.TryGetProperty("pickcenter", out var pickcenter)
            || pickcenter.ValueKind != JsonValueKind.Array
            || pickcenter.GetArrayLength() == 0)
            yield break;

        var entry = pickcenter[0];

        var book = entry.TryGetProperty("provider", out var provider)
                   && provider.TryGetProperty("name", out var providerName)
            ? providerName.GetString() ?? "ESPN"
            : "ESPN";

        // Each side's open sits beside its close in the same node and is decoded the same way. It
        // rides on the close it belongs to, so a side ESPN did not close is not opened either.

        // Moneyline: a price per side, no line — not even an opening one.
        foreach (var (node, outcome) in Sides(entry, "moneyline", homeTeam, awayTeam))
        {
            if (ReadClose(node, Markets.Moneyline, out _, out var price))
                yield return new CanonicalOdds(sourceGameId, book, Markets.Moneyline, outcome, null, price, capturedAt)
                {
                    Opening = ReadOpen(node, Markets.Moneyline)
                };
        }

        // Spread: a handicap and a price per side.
        foreach (var (node, outcome) in Sides(entry, "pointSpread", homeTeam, awayTeam))
        {
            if (ReadClose(node, Markets.Spread, out var line, out var price) && line is not null)
                yield return new CanonicalOdds(sourceGameId, book, Markets.Spread, outcome, line, price, capturedAt)
                {
                    Opening = ReadOpen(node, Markets.Spread)
                };
        }

        // Total: one number for the game, quoted from both sides.
        foreach (var (node, outcome) in Sides(entry, "total", "over", "under"))
        {
            if (ReadClose(node, Markets.Total, out var line, out var price) && line is not null)
                yield return new CanonicalOdds(sourceGameId, book, Markets.Total, outcome, line, price, capturedAt)
                {
                    Opening = ReadOpen(node, Markets.Total)
                };
        }
    }

    /// <summary>
    /// The close from the odds endpoint's shape: one item per provider, each with
    /// <c>homeTeamOdds</c>/<c>awayTeamOdds</c> carrying the moneyline and spread closes and a
    /// top-level <c>close</c> carrying the total and its two prices.
    ///
    /// <para>
    /// The pre-game book is read and the live one skipped: "ESPN Bet - Live Odds" is the number
    /// in play, which is the one thing a closing line must not be. Prices are the per-side
    /// closes, never the flatter <c>spreadOdds</c>/<c>overOdds</c>, which on this endpoint are
    /// the last live quote.
    /// </para>
    /// </summary>
    internal static IEnumerable<CanonicalOdds> ParseCoreOdds(
        JsonElement root, string sourceGameId, string homeTeam, string awayTeam, DateTimeOffset capturedAt)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            yield break;

        JsonElement? chosen = null;
        var book = "ESPN";

        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("provider", out var provider)
                       && provider.TryGetProperty("name", out var providerName)
                ? providerName.GetString()
                : null;

            if (name is not null && name.Contains("live", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!item.TryGetProperty("close", out _)
                && !(item.TryGetProperty("homeTeamOdds", out var h) && h.TryGetProperty("close", out _)))
                continue;

            chosen = item;
            book = name ?? book;
            break;
        }

        if (chosen is not { } entry)
            yield break;

        // A fight's sides are athletes, not teams (ADR 0021); the block is the same shape either way.
        foreach (var (key, athleteKey, outcome) in new[]
                 {
                     ("homeTeamOdds", "homeAthleteOdds", homeTeam), ("awayTeamOdds", "awayAthleteOdds", awayTeam)
                 })
        {
            if (!(entry.TryGetProperty(key, out var side) || entry.TryGetProperty(athleteKey, out side))
                || !side.TryGetProperty("close", out var close))
                continue;

            // The side's open is a sibling of its close, in the same blocks; a side may state one
            // and not the other, which leaves the opening null rather than guessed.
            var open = side.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;

            if (TryAmerican(close, "moneyLine", out var ml))
                yield return new CanonicalOdds(sourceGameId, book, Markets.Moneyline, outcome, null, ml, capturedAt)
                {
                    Opening = OpeningOf(null, OpenPrice(open, "moneyLine"))
                };

            if (TryLine(close, "pointSpread", spread: true, out var spread) && TryAmerican(close, "spread", out var spreadPrice))
                yield return new CanonicalOdds(sourceGameId, book, Markets.Spread, outcome, spread, spreadPrice, capturedAt)
                {
                    Opening = OpeningOf(OpenLine(open, "pointSpread", spread: true), OpenPrice(open, "spread"))
                };
        }

        if (entry.TryGetProperty("close", out var game) && TryLine(game, "total", spread: false, out var total))
        {
            // The total's open is one number for the game, as its close is, with a price per side.
            var open = entry.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
            var openTotal = OpenLine(open, "total", spread: false);

            if (TryAmerican(game, "over", out var over))
                yield return new CanonicalOdds(sourceGameId, book, Markets.Total, "over", total, over, capturedAt)
                {
                    Opening = OpeningOf(openTotal, OpenPrice(open, "over"))
                };

            if (TryAmerican(game, "under", out var under))
                yield return new CanonicalOdds(sourceGameId, book, Markets.Total, "under", total, under, capturedAt)
                {
                    Opening = OpeningOf(openTotal, OpenPrice(open, "under"))
                };
        }
    }

    /// <summary>An opening price block on the odds endpoint, or null where the open states none.</summary>
    private static int? OpenPrice(JsonElement open, string name)
        => open.ValueKind == JsonValueKind.Object && TryAmerican(open, name, out var price) ? price : null;

    /// <summary>An opening line block on the odds endpoint, or null where the open states none.</summary>
    private static decimal? OpenLine(JsonElement open, string name, bool spread)
        => open.ValueKind == JsonValueKind.Object && TryLine(open, name, spread, out var line) ? line : null;

    /// <summary>The two halves as an opening, or null where neither was stated.</summary>
    private static CanonicalOpening? OpeningOf(decimal? line, int? price)
        => line is null && price is null ? null : new CanonicalOpening(line, price);

    /// <summary>A price block on the odds endpoint: <c>{ "american": "-110", … }</c>.</summary>
    private static bool TryAmerican(JsonElement parent, string name, out int price)
    {
        price = 0;
        return parent.TryGetProperty(name, out var block)
               && TryParseAmerican(JsonRead.String(block, "american") ?? JsonRead.String(block, "alternateDisplayValue"), out price);
    }

    /// <summary>
    /// A line block on the odds endpoint: <c>{ "american": "-7.5" }</c>, or a total's "47.5". A
    /// pick'em reads as a spread of zero, as it does in the summary (<see cref="JsonRead.TryParseLine"/>).
    /// </summary>
    private static bool TryLine(JsonElement parent, string name, bool spread, out decimal line)
    {
        line = 0;

        return parent.TryGetProperty(name, out var block)
               && JsonRead.TryParseLine(JsonRead.String(block, "american") ?? JsonRead.String(block, "alternateDisplayValue"), spread, out line);
    }

    /// <summary>
    /// The two sides of one market, named as the platform names them. ESPN keys them
    /// home/away even for a total, where the sides are over/under.
    /// </summary>
    private static IEnumerable<(JsonElement Node, string Outcome)> Sides(
        JsonElement entry, string market, string first, string second)
    {
        if (!entry.TryGetProperty(market, out var block))
            yield break;

        var firstKey = market == "total" ? "over" : "home";
        var secondKey = market == "total" ? "under" : "away";

        if (block.TryGetProperty(firstKey, out var a))
            yield return (a, first);

        if (block.TryGetProperty(secondKey, out var b))
            yield return (b, second);
    }

    /// <summary>
    /// The closing line and price from one side of one market.
    ///
    /// Lines arrive decorated — "o8.5", "u8.5", "+1.5" — so the leading marker is stripped
    /// before parsing. A side missing its close is skipped rather than defaulted: a market
    /// ESPN did not price is not a market priced at zero.
    /// </summary>
    private static bool ReadClose(JsonElement side, string market, out decimal? line, out int price)
    {
        line = null;
        price = 0;

        if (!side.TryGetProperty("close", out var close))
            return false;

        if (!close.TryGetProperty("odds", out var oddsEl)
            || !TryParseAmerican(oddsEl.GetString(), out price))
            return false;

        line = ReadPickLine(close, market);

        return true;
    }

    /// <summary>
    /// What one side of one market opened at, from the <c>open</c> block beside its close, decoded
    /// as <see cref="ReadClose"/> decodes the close. Unlike the close, either half may stand alone:
    /// ESPN writes "OFF" for a price it took down, and the line opened at is still a fact. Null
    /// where the side states neither. A moneyline's open has no line, whatever the block says.
    /// </summary>
    private static CanonicalOpening? ReadOpen(JsonElement side, string market)
    {
        if (!side.TryGetProperty("open", out var open) || open.ValueKind != JsonValueKind.Object)
            return null;

        int? price = JsonRead.String(open, "odds") is { } raw && TryParseAmerican(raw, out var parsed) ? parsed : null;

        return OpeningOf(ReadPickLine(open, market), price);
    }

    /// <summary>
    /// The line in a pickcenter <c>open</c> or <c>close</c> block, parsed as the odds endpoint's is
    /// (<see cref="JsonRead.TryParseLine"/>): decorated lines stripped, and a pick'em a spread of
    /// zero. It used to refuse "PK", which dropped a pick'em's spread from the summary — and, with
    /// the moneyline read, the odds endpoint was never asked, so the spread stayed missing. A
    /// moneyline has no line, whatever the block says.
    /// </summary>
    private static decimal? ReadPickLine(JsonElement block, string market)
        => market != Markets.Moneyline
           && JsonRead.TryParseLine(JsonRead.String(block, "line"), market == Markets.Spread, out var line)
            ? line
            : null;

    /// <summary>
    /// An American price as ESPN writes it: "+130", "-157", and occasionally "EVEN".
    /// </summary>
    internal static bool TryParseAmerican(string? raw, out int price)
    {
        price = 0;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();

        // "EVEN" is a real quote meaning +100, and dropping it would silently lose a market.
        if (text.Equals("EVEN", StringComparison.OrdinalIgnoreCase))
        {
            price = 100;
            return true;
        }

        return int.TryParse(text.TrimStart('+'), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out price);
    }
}
