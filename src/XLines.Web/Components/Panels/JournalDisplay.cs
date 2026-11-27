using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Ingestion.Services;
using XLines.Web.Services;

namespace XLines.Web.Components.Panels;

/// <summary>
/// What the Journal's rows say, as functions of the row alone: a result's tag state, why a parlay
/// is stuck, the club a selection backs, a bet in words and the close its CLV was measured
/// against. No component state, so each is tested on its own (JournalDisplayTests); the Journal
/// and its parlays ledger read them through <c>using static</c>.
/// </summary>
public static class JournalDisplay
{
    /// <summary>Away then home, by abbreviation, valued by name. Null until both teams are loaded.</summary>
    public static IReadOnlyList<DeskSwitchOption<string>>? TeamSides(Game? game)
        => game is { AwayTeam: { } away, HomeTeam: { } home }
            ? [new(away.Name, away.Abbrev, away.Name), new(home.Name, home.Abbrev, home.Name)]
            : null;

    /// <summary>Why a pending parlay whose legs are all in has not settled, if that is the case.</summary>
    public static string? Stuck(Parlay parlay)
        => parlay.Legs.All(l => l.Result != EntryResult.Pending)
            ? ParlayGrading.Grade(parlay, parlay.Legs).Reason
            : null;

    /// <summary>
    /// The club a selection names, when it names one: a moneyline or a spread on a tracked game
    /// is a side, and its outcome is that side's name. A total names no club, and a bout's
    /// fighters carry no crest, so both answer null. Read off the row's own game, which the
    /// journal's read already brings with its teams and sport.
    /// </summary>
    public static Team? SelectedClub(JournalEntry e)
    {
        if (e.Game is not { } game || FightSports.IsFight(game.Sport?.Key))
            return null;

        return string.Equals(e.Outcome, game.AwayTeam?.Name, StringComparison.OrdinalIgnoreCase) ? game.AwayTeam
            : string.Equals(e.Outcome, game.HomeTeam?.Name, StringComparison.OrdinalIgnoreCase) ? game.HomeTeam
            : null;
    }

    public static string Describe(JournalEntry e)
        => e.FreeTextMarket ?? $"{e.Outcome}{(e.LineTaken is { } l ? " " + OddsText.Line(l) : "")} {SelectionText.MarketName(e.Market)}";

    public static ClvResult? Clv(JournalEntry entry) => PerformanceAnalytics.ComputeClv(entry);

    /// <summary>"Took -1.5 -110; closed -2.5 -110 at fanduel (no close for draftkings)" — what was compared.</summary>
    public static string ClvTitle(JournalEntry e)
    {
        static string At(decimal? points, decimal price)
            => points is { } p ? $"{p:+0.#;-0.#} {OddsText.Price(price)}" : OddsText.Price(price);

        var close = At(e.ClosingPoints, e.ClosingPrice!.Value);
        var book = string.Equals(e.ClosingBook, e.Book, StringComparison.OrdinalIgnoreCase) || e.ClosingBook is null
            ? ""
            : $" at {e.ClosingBook} (no close for {e.Book})";

        var fair = PerformanceAnalytics.ComputeClv(e) is { EvAtClose: { } ev }
            ? $". Worth {ev * 100:+0.0;−0.0}% at the fair close ({(e.ClosingFairBasis == FairValue.SharpBook ? "Pinnacle" : "consensus")}, no-vig)"
            : "";

        return $"Took {At(e.LineTaken, e.PriceTaken)}; closed {close}{book}{fair}";
    }

    public static DeskState ResultState(EntryResult result) => result switch
    {
        EntryResult.Win => DeskState.Positive,
        EntryResult.Loss => DeskState.Negative,
        EntryResult.Push => DeskState.Info,
        EntryResult.Void => DeskState.Neutral,
        _ => DeskState.Warning
    };
}
