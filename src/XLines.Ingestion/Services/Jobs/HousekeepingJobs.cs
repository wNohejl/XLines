using Microsoft.Extensions.DependencyInjection;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>Settle finished games, <see cref="IngestionJobs.Settle"/>: grades the journal and resolves CLV.</summary>
internal sealed class SettleJob(IServiceScopeFactory scopeFactory) : IIngestionJob
{
    public bool Matches(string key) => key == IngestionJobs.Settle;

    /// <summary>
    /// Last, after the lines: the menu draws its groups in the order they first appear, and
    /// housekeeping, which spends nothing, is the foot of it (JobGroups).
    /// </summary>
    public ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
        => JobSupport.Describe(new IngestionJob(
            Key: IngestionJobs.Settle,
            Label: "Settle finished games",
            Description: "Grades journal entries whose games are final and resolves their CLV.",
            EstimatedRequests: 0,
            Available: true,
            Unavailable: null,
            Group: JobGroups.Housekeeping));

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settlement = scope.ServiceProvider.GetRequiredService<SettlementService>();

        var summary = await settlement.SettleAsync(ct);

        return new JobOutcome(IngestionJobs.Settle, true, summary.Graded + summary.ClvResolved, 0,
            $"{summary.Graded} graded, {summary.Voided} voided, {summary.ParlaysSettled} parlays, {summary.ClvResolved} CLV resolved, {summary.LeftPending} pending.");
    }
}

/// <summary>
/// The jobs only a command line can run, refused here by name with their usage rather than
/// reported as unknown: a range is nothing without its league and dates (<see cref="RangePull"/>),
/// and the journal jobs take a file, which only the Worker's command line names. Never on the menu.
/// </summary>
internal sealed class CommandLineJobs : IIngestionJob
{
    public bool Matches(string key)
        => key is IngestionJobs.EspnRange or JournalTransfer.ExportJob or JournalTransfer.ImportJob;

    public ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct) => JobSupport.Nothing();

    public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        => Task.FromResult(new JobOutcome(run.Key, false, 0, 0,
            run.Key == IngestionJobs.EspnRange ? RangePull.Usage : JournalTransfer.Usage));
}
