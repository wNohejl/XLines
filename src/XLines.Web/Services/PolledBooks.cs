using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using Microsoft.Extensions.Options;

namespace XLines.Web.Services;

/// <summary>
/// The books the desk polls for prices right now, for the surfaces that account for every one of
/// them (<c>BookCoverage</c>): the book tables' "priced of polled" and absent line, the price
/// tooltip's count, and the Board's unpriced rows.
///
/// <para>
/// Read through <see cref="OddsFeedStatus"/>, the one answer Ops shows, so the tables cannot
/// disagree with the Odds feeds card: the books of each odds provider that is live (registered,
/// with its key), in configuration's order. Read in a fresh scope each time, because a key typed
/// into Ops makes a provider live without a restart and the source registry is scoped.
/// </para>
///
/// <para>
/// Empty when no odds provider is live (no key, or ESPN alone) or when the host has no ingestion
/// services at all (a test bench): empty means unknown, and every surface then draws exactly as
/// it did before polled books were counted.
/// </para>
/// </summary>
public static class PolledBooks
{
    /// <summary>The polled books' keys, or empty when no odds provider is live.</summary>
    public static IReadOnlyList<string> Read(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        return Read(scope.ServiceProvider);
    }

    /// <summary>The same, from a scope the caller already holds.</summary>
    public static IReadOnlyList<string> Read(IServiceProvider services)
    {
        if (services.GetService<SourceRegistry>() is not { } registry
            || services.GetService<IOptions<IngestionOptions>>() is not { } options)
            return [];

        return Of(new OddsFeedStatus(options, registry).Describe());
    }

    /// <summary>The books of the live providers, each once, in their configured order.</summary>
    public static IReadOnlyList<string> Of(IEnumerable<OddsProviderState> feeds)
        => feeds.Where(f => f.IsLive)
            .SelectMany(f => f.Books)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            // One book once, however many keys it is filed under (caesars, williamhill_us).
            .DistinctBy(b => XLines.Web.Components.Panels.BookNames.Display(b), StringComparer.OrdinalIgnoreCase)
            .ToList();
}
