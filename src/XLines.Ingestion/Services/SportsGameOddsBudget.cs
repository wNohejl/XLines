using System.Globalization;
using XLines.Data;
using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services;

/// <summary>
/// SportsGameOdds' allowance (ADR 0033), apart from The Odds API's credits: objects — one per event
/// a response returns — over the provider's rolling window (30 days from first use, not the calendar
/// month), counted from this source's own run rows. The provider reports no remainder on its
/// responses, so this is this database's count alone; a second machine on the same key is not seen,
/// which is what <see cref="SportsGameOddsOptions.ObjectReserve"/> is wide enough to absorb.
/// </summary>
public sealed class SportsGameOddsBudget(XLinesDbContext db, IOptions<IngestionOptions> options, TimeProvider clock)
{
    private readonly SportsGameOddsOptions _config = options.Value.SportsGameOdds;

    /// <summary>The allowance now: objects spent in the trailing window, the budget and the reserve.</summary>
    public async Task<ObjectAllowance> ReadAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var since = now - _config.BudgetWindow;

        var spent = await db.IngestionRuns
            .Where(r => r.Source!.Key == SportsGameOddsAdapter.SourceKey && r.StartedAt >= since)
            .SumAsync(r => (int?)r.CreditsSpent, ct) ?? 0;

        return new ObjectAllowance(_config.MonthlyObjects, spent, _config.ObjectReserve, _config.MaxEventsPerPull,
            (int)Math.Round(_config.BudgetWindow.TotalDays));
    }
}

/// <summary>
/// One pull's standing against SportsGameOdds' objects. Pure, so the rule is tested alone.
/// </summary>
/// <param name="Budget">Objects the plan allows in the window (2,500 on the free plan).</param>
/// <param name="Spent">Objects this database recorded spending in the window.</param>
/// <param name="Reserve">Objects no pull may spend.</param>
/// <param name="PerPull">The most events one pull may return.</param>
/// <param name="WindowDays">The window's length, for the sentences.</param>
public sealed record ObjectAllowance(int Budget, int Spent, int Reserve, int PerPull, int WindowDays)
{
    public int Remaining => Math.Max(0, Budget - Spent);

    /// <summary>
    /// The most objects the next pull may spend: the per-pull cap, cut to what is left above the
    /// reserve. Zero refuses it. The adapter is handed this as its event ceiling, so a pull cannot
    /// spend past the reserve whatever the provider lists.
    /// </summary>
    public int Ceiling => Math.Max(0, Math.Min(PerPull, Remaining - Reserve));

    /// <summary>
    /// What a pull expecting <paramref name="expected"/> events may do: refused when nothing is left
    /// above the reserve; asked first when it would be cut short; otherwise run, its price stated.
    /// </summary>
    public CreditCheck Check(int expected)
    {
        if (Ceiling == 0)
            return CreditCheck.Refuse(
                $"{Remaining:N0} of {Budget:N0} SportsGameOdds objects are left in the last {WindowDays} days, "
                + $"and {Reserve:N0} are held in reserve. Older pulls fall out of the window day by day.");

        if (expected > Ceiling)
            return CreditCheck.Ask(
                $"About {expected:N0} games are listed and this pull stops at {Ceiling:N0} objects "
                + $"to keep {Reserve:N0} in reserve. Pull the first {Ceiling:N0}?");

        return CreditCheck.Go;
    }

    /// <summary>"14 objects · 2,286 left" — as the menu states the price.</summary>
    public string Price(int expected)
        => $"{expected.ToString("N0", CultureInfo.InvariantCulture)} {(expected == 1 ? "object" : "objects")} · {Remaining:N0} left";
}
