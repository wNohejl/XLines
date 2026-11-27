namespace XLines.Web.Services;

/// <summary>Which season a subject window (a team, a player) shows, shared by the windows and their data services.</summary>
public static class SubjectSeasons
{
    /// <summary>
    /// The season a load shows: the one asked for while the subject has played in it, else the
    /// newest held (seasons come newest first), or none for a subject with no season on record.
    /// </summary>
    public static int? Pick(IReadOnlyList<int> seasons, int? asked)
        => asked is { } year && seasons.Contains(year)
            ? year
            : seasons.Count > 0 ? seasons[0] : null;
}
