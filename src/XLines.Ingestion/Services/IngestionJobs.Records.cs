namespace XLines.Ingestion.Services;

// The standings the catalogue's helpers return, nested under IngestionJobs where their callers
// name them (IngestionJobs.ScoresOwed and the rest), in a file of their own.
public partial class IngestionJobs
{
    /// <summary>
    /// Finals without the close they are owed; how many of them a press would ask now (within a
    /// run's ceiling); and how many are set aside until their retry date.
    /// </summary>
    internal readonly record struct OwedCloses(int WithoutClose, int ToAsk, int SetAside = 0);

    /// <summary>Each league's scores standing, and the games across them no scores call can finish.</summary>
    public sealed record ScoresOwed(IReadOnlyDictionary<string, LeagueScores> Leagues, int Unrecoverable);

    /// <summary>Games a scores call could finish now, and games still being played.</summary>
    public readonly record struct LeagueScores(int Owed, int Playing);
}
