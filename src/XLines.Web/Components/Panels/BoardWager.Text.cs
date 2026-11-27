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
    /// <summary>The price row's first line: the side and its line, "Bills +2.5", "Over 47.5", "Chiefs".</summary>
    private static string EchoSide(WagerOffer offer)
        => offer.Line is { } line
            ? $"{SelectionText.Short(offer.Outcome)} {OddsMath.FormatLine(line, offer.Outcome)}"
            : SelectionText.Short(offer.Outcome);

    /// <summary>
    /// The price row's second line: the market by its full name and where the side stands in the
    /// game — "Spread · at Chiefs" for the away side, "Moneyline · vs Bills" for the home side,
    /// "Total · Bills at Chiefs" for a total.
    /// </summary>
    private string EchoDetail(string market, string outcome)
    {
        var name = market switch
        {
            Markets.Moneyline => "Moneyline",
            Markets.Spread => "Spread",
            Markets.Total => "Total",
            _ => market
        };

        if (Row?.Game is not { } game)
            return name;

        var away = game.AwayTeam?.Name;
        var home = game.HomeTeam?.Name;

        // A fight has no home: either corner is "vs" the other, billed red corner first (ADR 0021).
        if (FightSports.IsFight(game.Sport?.Key))
            return outcome == away && home is not null ? $"{name} · vs {SelectionText.Short(home)}"
                 : outcome == home && away is not null ? $"{name} · vs {SelectionText.Short(away)}"
                 : away is not null && home is not null ? $"{name} · {SelectionText.Short(home)} vs {SelectionText.Short(away)}"
                 : name;

        return outcome == away && home is not null ? $"{name} · at {SelectionText.Short(home)}"
             : outcome == home && away is not null ? $"{name} · vs {SelectionText.Short(away)}"
             : away is not null && home is not null ? $"{name} · {SelectionText.Short(away)} at {SelectionText.Short(home)}"
             : name;
    }

    /// <summary>
    /// The echo under a typed price: the side at the line typed, not the line fetched. It read the
    /// fetched side's label, so a -6.5 typed over a -4.5 board still said -4.5 under the price,
    /// and the number being logged looked as if it had not taken.
    /// </summary>
    private string? TypedEcho
        => Side is { } side ? _typed.Echo(side.Market, side.Outcome, HasLine, side.Offer?.Line) : null;

    /// <summary>
    /// Why a price has no value read, said rather than left as a bare "no fair price": a line
    /// typed off the fetched one has nothing to be measured against, since the fair price is the
    /// books' consensus on the fetched number only.
    /// </summary>
    private string NoFairWhy(WagerOffer offer)
    {
        var fetched = Side?.Offer;

        if (offer.Manual && fetched?.Line is { } at && offer.Line is { } typed && typed != at)
            return $"No fair price on {OddsMath.FormatLine(typed, offer.Outcome)}: the books' fair price is for "
                   + $"{OddsMath.FormatLine(at, offer.Outcome)}, so value at your line is unread, not zero.";

        if (offer.Manual && fetched is null)
            return "No fair price: no book the desk scans prices this side, so value is unread, not zero.";

        return "No fair price on this number: fewer than two books quote it, so value is unread, not zero.";
    }

    private static string Captured(WagerOffer offer)
    {
        if (offer.IsClosing)
            return "closing price";

        // The same words as the price-age check beneath it ("1h 49m", not "1.8h"), so the two
        // lines about one number never disagree in form.
        var age = DateTimeOffset.UtcNow - offer.CapturedAt;
        return age < TimeSpan.FromMinutes(1) ? "captured just now" : $"captured {Checks.Span(age)} ago";
    }

    private static string LinePrice(WagerOffer offer)
        => (offer.Line is { } line ? OddsMath.FormatLine(line, offer.Outcome) + " " : "") + Price(offer.PriceAmerican);
}
