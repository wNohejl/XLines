using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>
/// Price scans: every configured league, <see cref="JobKeys.OddsLines"/>, or one,
/// <c>odds:lines:{sport}</c> — the entry the menu lists per league.
/// </summary>
internal sealed class OddsLinesJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    /// <summary>
    /// The league an on-call drill should exercise: the one actually in play.
    ///
    /// <para>
    /// A drill with no fault injected performs a real pull against a metered provider, so the
    /// rows it buys should be rows somebody is about to read. Naming a league in the code
    /// instead meant the drill bought fixtures weeks away while the slate on screen went
    /// unpriced, and the volume it wrote was measured against pulls for a different sport.
    /// </para>
    ///
    /// <para>
    /// A league nobody configured is never chosen however busy it looks, which is the rule
    /// <see cref="OddsLinesJob"/> already applies to a named sport: quietly scanning a league
    /// the operator did not ask for is what a credit budget cannot absorb.
    /// </para>
    /// </summary>
    /// <param name="upcomingBySport">Games inside the movement window, keyed by sport slug.</param>
    /// <param name="configured">The configured leagues, in preference order.</param>
    /// <returns>The league to drill, or null when none is configured.</returns>
    public static string? DrillSport(
        IReadOnlyDictionary<string, int> upcomingBySport, IReadOnlyList<string> configured)
    {
        if (configured.Count == 0)
            return null;

        var busiest = upcomingBySport
            .Where(kv => kv.Value > 0
                         && configured.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        // Match the configured spelling rather than whatever case the games table holds, so the
        // key handed to an adapter is the one config named.
        return busiest is null
            ? configured[0]
            : configured.First(s => string.Equals(s, busiest, StringComparison.OrdinalIgnoreCase));
    }

    public bool Matches(string key)
        => key == JobKeys.OddsLines || key.StartsWith(JobKeys.OddsLinesPrefix, StringComparison.Ordinal);

    /// <summary>
    /// One entry per sport rather than one for all of them.
    ///
    /// "Fresh lines" used to scan every configured league at once, which on a credit-billed
    /// provider is the whole month's allowance being spent on leagues that are out of season
    /// and quoting nothing. Naming the sport makes the price legible before the press and lets
    /// an operator buy only the league they are working.
    /// </summary>
    public ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<IngestionJob>>(
            menu.Sports.Select(sport => LinesJob(sport, menu.Registry, menu.Allowance)).ToList());

    public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        => run.Key == JobKeys.OddsLines
            ? RunOddsAsync(null, ct)
            : RunOddsAsync(run.Key[JobKeys.OddsLinesPrefix.Length..], ct, run.ClosingScanDay);

    /// <summary>The one definition of a league's lines entry, shared by the menu and the single read.</summary>
    public IngestionJob LinesJob(string sport, SourceRegistry registry, AllowanceState? allowance)
    {
        var odds = registry.OddsSources.Count;
        int? left = allowance is { Reported: true } ? allowance.Remaining : null;
        var resets = allowance?.ResetsOnText ?? "the 1st";
        var billsCredits = registry.FindOdds(Adapters.TheOddsApiAdapter.SourceKey) is not null;

        // What one sport's lines actually cost, in the unit the provider bills. Stated per sport
        // because that is how it is charged and how it is now spent.
        // The adapters say, per sport, from the markets they will actually ask for; the
        // configured figure stands in only for a source that declares nothing.
        var credits = LineCredits(sport, registry);

        var names = registry.OddsSources.SelectMany(s => s.MarketsFor(sport)).Distinct()
            .Select(m => m switch { Markets.Moneyline => "moneyline", Markets.Spread => "spread", Markets.Total => "total", _ => m })
            .ToList();
        var markets = names.Count == 0 ? "Lines" : char.ToUpperInvariant(names[0][0]) + string.Join(", ", names)[1..];

        var check = billsCredits && allowance is not null ? allowance.Check(credits) : CreditCheck.Go;
        var priced = _options.PricedSportsWith(registry.Espn is not null).Contains(sport, StringComparer.OrdinalIgnoreCase);
        var league = sport.ToUpperInvariant();

        return new IngestionJob(
            Key: JobKeys.OddsLinesPrefix + sport,
            Label: $"Pull lines — {league}",
            Description: $"{markets} across every book for {league} "
                         + "games that have not started.",
            EstimatedRequests: odds,
            EstimatedCredits: credits,
            Available: odds > 0 && priced && check.Allowed,
            Unavailable: odds == 0 ? IngestionJobs.NoOddsKey
                : !priced ? $"{league} is priced only against bouts ESPN holds, and ESPN is off: a pull would spend {credits} credits and attach nothing."
                : check.Refusal,
            Group: JobGroups.Lines,
            Confirm: check.Question,
            Hint: billsCredits
                ? $"Calls The Odds API's odds for {league}: {credits} credits, one per market at one region. "
                  + JobSupport.MonthText(allowance, resets)
                : odds == 0
                    ? $"Pulls {league} prices from The Odds API, a credit per market. " + IngestionJobs.NoOddsKey
                    : null,
            CreditsLeft: billsCredits ? left : null,
            Metered: true,
            NeedsKey: odds == 0);
    }

    /// <summary>
    /// What one league's lines bill, in credits: the adapters' own forecast from the markets they
    /// ask for, and the configured per-sport figure only for a source that declares nothing.
    /// </summary>
    public int LineCredits(string sport, SourceRegistry registry)
    {
        var declared = registry.OddsSources.Sum(s => s.CreditsPerScan(sport));
        return declared > 0 ? declared : registry.OddsSources.Count * _options.LinePolling.CreditsPerSportPerScan;
    }

    /// <summary>
    /// A price scan, over one sport or over every configured one.
    /// </summary>
    /// <param name="onlySport">
    /// The single league to scan, or null for all of them. A named sport is refused rather than
    /// silently widened when it is not configured — quietly scanning four leagues because one was
    /// misspelled is exactly the accident a credit budget cannot absorb.
    /// </param>
    /// <param name="closingScanDay">The league day a closing scan counts against; null for any other scan.</param>
    private async Task<JobOutcome> RunOddsAsync(string? onlySport, CancellationToken ct, DateOnly? closingScanDay = null)
    {
        var jobKey = onlySport is null ? JobKeys.OddsLines : JobKeys.OddsLinesPrefix + onlySport;

        // The leagues a scan can price: every configured one with ESPN on, as always; without ESPN a
        // fought league is left out, since its fights attach only to bouts ESPN holds (ADR 0026).
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var priced = _options.PricedSportsWith(registry.Espn is not null);

        var sports = onlySport is null
            ? priced
            : priced
                .Where(s => string.Equals(s, onlySport, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (sports.Length == 0)
            return new JobOutcome(jobKey, false, 0, 0,
                _options.EffectiveSports.Contains(onlySport, StringComparer.OrdinalIgnoreCase)
                    ? $"{onlySport!.ToUpperInvariant()} is priced only against bouts ESPN holds, and ESPN is off."
                    : $"'{onlySport}' is not a configured sport.");

        var rows = 0;
        var failures = 0;
        var credits = 0;
        var runIds = new List<long>();

        var ingestion = scope.ServiceProvider.GetRequiredService<OddsIngestionService>();
        var initialiser = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

        await initialiser.EnsurePartitionsAsync(ct);

        foreach (var source in registry.OddsSources)
        {
            foreach (var sportKey in sports)
            {
                if (ct.IsCancellationRequested)
                    break;

                var outcome = await ingestion.IngestAsync(source, sportKey, jobKey, ct, closingScanDay);
                rows += outcome.RowsIngested;
                runIds.Add(outcome.RunId);

                if (outcome.Status == RunStatus.Failed)
                    failures++;
            }
        }

        // What it actually cost, read back from the runs just written rather than estimated.
        // The provider reports its own billing in response headers, so this is their number.
        //
        // By the ids of this pull's own runs, as the scores pull counts. It used to sum every run of
        // the job key from the last five minutes, so a second press inside five minutes reported the
        // first press's credits again, and so did a pull on the other host sharing the database.
        credits = await db.IngestionRuns
            .Where(r => runIds.Contains(r.Id))
            .SumAsync(r => (int?)r.CreditsSpent, ct) ?? 0;

        var detail = credits > 0
            ? $"{rows:N0} price {(rows == 1 ? "move" : "moves")}, {credits} credits spent."
            : null;

        return new JobOutcome(jobKey, failures == 0, rows, failures, detail);
    }
}
