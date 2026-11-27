using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>One league's prices from the second source, SportsGameOdds (ADR 0033), <c>sgo:lines:{sport}</c>: on request only.</summary>
internal sealed class SgoLinesJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    TimeProvider clock) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key) => key.StartsWith(IngestionJobs.SgoLinesPrefix, StringComparison.Ordinal);

    /// <summary>The second source's pulls, only where its key is held (ADR 0033): one a league it covers.</summary>
    public async ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
    {
        if (menu.Registry.SportsGameOdds is null
            || menu.Services.GetService<SportsGameOddsBudget>() is not { } objects)
            return JobSupport.None;

        var standing = await objects.ReadAsync(ct);
        var db = menu.Services.GetRequiredService<XLinesDbContext>();
        var jobs = new List<IngestionJob>();

        foreach (var sport in menu.Sports.Where(Adapters.SportsGameOddsAdapter.Covers))
            jobs.Add(Entry(sport, standing, await SgoExpectedAsync(db, sport, ct)));

        return jobs;
    }

    /// <summary>On request only, never with a closing-scan day: a closing scan of this key is no job at all.</summary>
    public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        => run.ClosingScanDay is null
            ? RunSgoAsync(run.Key[IngestionJobs.SgoLinesPrefix.Length..], ct)
            : Task.FromResult(JobSupport.Unknown(run.Key));

    /// <summary>
    /// The objects a league's pull is expected to spend — one per game the desk holds that starts
    /// within the look-ahead and has not started — capped at one pull's ceiling. A league with
    /// none on the desk is priced at the ceiling, which is what the provider could list.
    /// </summary>
    private async Task<int> SgoExpectedAsync(XLinesDbContext db, string sport, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var until = now + _options.SportsGameOdds.LookAhead;
        var cap = Math.Max(0, _options.SportsGameOdds.MaxEventsPerPull);

        var listed = await db.Games.CountAsync(g => g.Sport!.Key == sport
                                                    && g.StartsAt > now && g.StartsAt <= until
                                                    && g.Status != GameStatus.Postponed, ct);

        return listed == 0 ? cap : Math.Min(listed, cap);
    }

    /// <summary>One league's second-source pull, its price in objects stated before the press.</summary>
    private IngestionJob Entry(string sport, ObjectAllowance standing, int expected)
    {
        var league = sport.ToUpperInvariant();
        var check = standing.Check(expected);
        var books = string.Join(", ", _options.SportsGameOdds.RequestedBooks
            .Select(Adapters.SportsGameOddsAdapter.CanonicalBook)
            .Select(b => b == "williamhill_us" ? "Caesars" : b));

        return new IngestionJob(
            Key: IngestionJobs.SgoLinesPrefix + sport,
            Label: $"Pull {books} — {league}",
            Description: $"Moneyline, spread and total from {books} for {league} games in the next "
                         + $"{_options.SportsGameOdds.LookAhead.TotalDays:N0} days, from SportsGameOdds on your own key.",
            EstimatedRequests: 1,
            EstimatedCredits: expected,
            Available: check.Allowed,
            Unavailable: check.Refusal,
            Group: JobGroups.Lines,
            Confirm: check.Question,
            Hint: $"Calls SportsGameOdds' events for {league}: one object per game returned, about {expected:N0}, "
                  + $"at most {standing.Ceiling:N0}. {standing.Remaining:N0} of {standing.Budget:N0} objects are left in the last "
                  + $"{standing.WindowDays} days, {standing.Reserve:N0} held in reserve. None of The Odds API's credits are spent.",
            CreditsLeft: standing.Remaining,
            Metered: true,
            CostUnit: "object");
    }

    /// <summary>
    /// One league's prices from SportsGameOdds (ADR 0033), on request only: through the same
    /// ingestion as every book, with the pull's events capped at what the allowance has left above
    /// its reserve, so it cannot spend past it whatever the provider lists. A failure — a refused
    /// key, a rate limit, a payload that cannot be read — is the run's failure, stated, and writes
    /// nothing.
    /// </summary>
    private async Task<JobOutcome> RunSgoAsync(string sportKey, CancellationToken ct)
    {
        var jobKey = IngestionJobs.SgoLinesPrefix + sportKey;

        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();

        if (registry.SportsGameOdds is not { } source
            || scope.ServiceProvider.GetService<SportsGameOddsBudget>() is not { } budget)
            return new JobOutcome(jobKey, false, 0, 0, IngestionJobs.NoSportsGameOddsKey);

        var sport = _options.EffectiveSports.FirstOrDefault(s => string.Equals(s, sportKey, StringComparison.OrdinalIgnoreCase));
        if (sport is null)
            return new JobOutcome(jobKey, false, 0, 0, $"'{sportKey}' is not a configured sport.");

        if (!Adapters.SportsGameOddsAdapter.Covers(sport))
            return new JobOutcome(jobKey, false, 0, 0,
                $"SportsGameOdds' free plan does not carry {sport.ToUpperInvariant()}; it covers NFL, NBA and MLB here.");

        var standing = await budget.ReadAsync(ct);
        if (standing.Ceiling == 0)
            return new JobOutcome(jobKey, false, 0, 0, standing.Check(1).Refusal);

        source.EventCeiling = standing.Ceiling;

        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsurePartitionsAsync(ct);

        var outcome = await scope.ServiceProvider.GetRequiredService<OddsIngestionService>()
            .IngestAsync(source, sport, jobKey, ct);

        var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();
        var spent = await db.IngestionRuns.Where(r => r.Id == outcome.RunId).Select(r => r.CreditsSpent).FirstOrDefaultAsync(ct);
        var left = Math.Max(0, standing.Remaining - spent);
        var league = sport.ToUpperInvariant();

        if (outcome.Status == RunStatus.Failed)
            return new JobOutcome(jobKey, false, 0, 1,
                $"{league} from SportsGameOdds failed: {outcome.Error} {spent:N0} objects spent, {left:N0} left.");

        return new JobOutcome(jobKey, true, outcome.RowsIngested, 0,
            $"{league} from SportsGameOdds: {outcome.RowsIngested:N0} price {(outcome.RowsIngested == 1 ? "move" : "moves")}, "
            + $"{spent:N0} {(spent == 1 ? "object" : "objects")} spent, {left:N0} left."
            + (outcome.Status == RunStatus.Partial && outcome.Error is { } why ? " " + why : ""));
    }
}
