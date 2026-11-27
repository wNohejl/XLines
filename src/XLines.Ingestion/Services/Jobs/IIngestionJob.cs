using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Ingestion.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>
/// One job, or one family of keys (<c>odds:lines:{sport}</c>), in the catalogue
/// <see cref="IngestionJobs"/> fronts: which keys it answers to, how the pull menu lists it, and
/// what a run does. The run bookkeeping around every job — timing, the log line, the catch — is
/// the facade's, so a job body never repeats it.
/// </summary>
internal interface IIngestionJob
{
    /// <summary>Whether this handler runs <paramref name="key"/>. Every key matches at most one handler.</summary>
    bool Matches(string key);

    /// <summary>The menu entries this handler contributes, in the order the menu lists them; none where it lists none.</summary>
    ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct);

    /// <summary>Runs the job <see cref="JobRun.Key"/> names.</summary>
    Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct);
}

/// <summary>
/// What every entry on the menu is described against, read once per menu: the scope's services
/// and registry, the configured leagues, the stats and closing-line sources counted, whether the
/// menu is ESPN's or the default source's, and The Odds API's month.
/// </summary>
internal sealed record JobMenu(
    IServiceProvider Services,
    SourceRegistry Registry,
    string[] Sports,
    bool Espn,
    int Stats,
    int Closers,
    AllowanceState? Allowance,
    int? Left,
    string Resets);

/// <summary>One run asked for: the key, and the league day a closing scan counts against (null for any other run).</summary>
internal readonly record struct JobRun(string Key, DateOnly? ClosingScanDay = null);

/// <summary>What more than one job reads, in one place.</summary>
internal static class JobSupport
{
    public static readonly IReadOnlyList<IngestionJob> None = [];

    public static ValueTask<IReadOnlyList<IngestionJob>> Describe(params IngestionJob[] jobs)
        => ValueTask.FromResult<IReadOnlyList<IngestionJob>>(jobs);

    public static ValueTask<IReadOnlyList<IngestionJob>> Nothing() => ValueTask.FromResult(None);

    /// <summary>The refusal for a key no handler runs.</summary>
    public static JobOutcome Unknown(string key) => new(key, false, 0, 0, $"Unknown job '{key}'.");

    /// <summary>
    /// The Odds API account's month as a scope reads it, or null where no reader is registered (a
    /// host or test that leaves the allowance out) or no credit-metered row is held yet.
    /// </summary>
    public static async Task<AllowanceState?> ReadAllowanceAsync(IServiceProvider provider, CancellationToken ct)
        => provider.GetService<CreditAllowance>() is { } reader ? await reader.ReadAsync(ct) : null;

    /// <summary>
    /// "The free month is 500 credits; it resets on Dec 1." — the configured budget, not the free
    /// plan's figure, since a key on a paid plan carries more. Where no row has been read the free
    /// plan's 500 stands in, which is what the seed writes.
    /// </summary>
    public static string MonthText(AllowanceState? allowance, string resets)
        => $"The free month is {(allowance?.Budget ?? 500):N0} credits; it resets on {resets}.";

    /// <summary>Why the default source's jobs cannot run on this host, in the words the desk uses.</summary>
    public static string NoScheduleSource(SourceRegistry registry)
        => registry.Espn is not null
            ? "ESPN is the stats source on this host; its own jobs fetch schedules and scores."
            : "No Odds API key is held. Enter your free key in Ops → Odds feeds.";

    /// <summary>
    /// Schedules and, when asked, box scores.
    ///
    /// Both come from the same adapter call — a box-score pass fetches the scoreboard first
    /// anyway — so "today's games" and "yesterday's results" differ only in the date and in
    /// whether the per-game summaries are worth the requests. Before the games are played
    /// there is nothing to summarise, which is why the slate pass stops at the scoreboard.
    /// </summary>
    public static async Task<JobOutcome> RunStatsAsync(
        IServiceScopeFactory scopeFactory, IngestionOptions options,
        string jobKey, DateOnly date, bool schedulesOnly, CancellationToken ct)
    {
        var rows = 0;
        var failures = 0;

        foreach (var sportKey in options.EffectiveSports)
        {
            if (ct.IsCancellationRequested)
                break;

            // A scope per sport keeps the change tracker bounded, the lesson from the backfill.
            await using var scope = scopeFactory.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
            var stats = scope.ServiceProvider.GetRequiredService<StatsIngestionService>();

            foreach (var source in registry.StatsSources)
            {
                var outcome = schedulesOnly
                    ? await stats.IngestScheduleAsync(source, sportKey, jobKey, date, ct)
                    : await stats.IngestAsync(source, sportKey, jobKey, date, ct);

                rows += outcome.RowsIngested;

                if (outcome.Status == RunStatus.Failed)
                    failures++;
            }
        }

        return new JobOutcome(jobKey, failures == 0, rows, failures, null);
    }
}
