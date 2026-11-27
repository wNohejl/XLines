namespace XLines.Ingestion.Services;

/// <summary>One named pull, and what it would cost.</summary>
/// <param name="EstimatedCredits">
/// What the provider will bill, where it bills in credits rather than requests. Zero for the
/// unmetered ones. Quoted separately because requests and credits are not interchangeable — a
/// single call can cost several credits, and it is the credits that run out.
/// </param>
/// <param name="Group">
/// Which section of the pull menu the job sits in (<see cref="JobGroups"/>): the desk draws the
/// jobs of one group together under its heading, so the three kinds of pull read as three.
/// </param>
/// <param name="Confirm">
/// The question a metered pull asks before it spends — more than a trivial price, or into the
/// reserve (<see cref="CreditAllowance"/>). Null runs on the press.
/// </param>
/// <param name="Hint">What the pull calls, what it costs and when credits reset, for the entry's tooltip.</param>
/// <param name="CreditsLeft">The account's credits left this month, where the provider has said; drawn beside the price.</param>
/// <param name="Metered">
/// The pull spends a provider's credits. Its price is never stated as "free", even when it is
/// unknown because no source is registered to quote one.
/// </param>
/// <param name="NeedsKey">A metered pull that cannot be priced or run because no key is held.</param>
/// <param name="CostUnit">
/// What <paramref name="EstimatedCredits"/> counts, singular: "credit" for The Odds API, "object"
/// for SportsGameOdds (ADR 0033), whose allowance is a different thing on a different key.
/// </param>
public record IngestionJob(
    string Key,
    string Label,
    string Description,
    int EstimatedRequests,
    bool Available,
    string? Unavailable,
    int EstimatedCredits = 0,
    string? Group = null,
    string? Confirm = null,
    string? Hint = null,
    int? CreditsLeft = null,
    bool Metered = false,
    bool NeedsKey = false,
    string CostUnit = "credit");

/// <summary>
/// The pull menu's sections, in the order they are drawn. Today's slate first, because it is
/// pressed most; the catch-up pulls that fill what history still lacks; the lines, which are
/// the only pulls that spend credits; and the housekeeping that spends nothing.
/// </summary>
public static class JobGroups
{
    public const string Today = "Today";
    public const string CatchUp = "Catch up";
    /// <summary>
    /// Every pull that spends credits — lines, and the default source's scores — and nothing else, so
    /// a metered pull is told apart from a free one by where it sits as well as by its price.
    /// </summary>
    public const string Lines = "Costs credits";
    public const string Housekeeping = "Housekeeping";
}

/// <summary>What one run of a job did.</summary>
/// <param name="More">
/// The job left work a further run would do — a backport taken a batch at a time. False for a job
/// that does all it can in one run.
/// </param>
public record JobOutcome(string Key, bool Succeeded, int Rows, int Failures, string? Detail, bool More = false);
