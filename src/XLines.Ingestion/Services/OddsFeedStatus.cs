using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services;

/// <summary>
/// Which odds providers are actually feeding the desk, and why the others are not.
///
/// <para>
/// Odds providers fail to run for undramatic reasons — disabled in configuration, or enabled
/// with an empty key — and every one of them looks identical from the outside: no prices. The
/// adapter is not even registered, so nothing appears in the run log to explain the silence,
/// and "did my key work?" becomes a question you answer by reading source.
/// </para>
///
/// <para>
/// This reports the same decision the DI registration makes, from the same options, so the
/// answer on screen cannot drift from what the container did.
/// </para>
///
/// <para>
/// A configured list can also buy less than it names, silently: a book the provider sells only
/// on a paid plan comes back absent, and a list past ten is not sent by name at all
/// (<see cref="TheOddsApiBooks"/>). Those are stated as <see cref="OddsProviderState.Notes"/>, and
/// appended to the reason the Ops window already draws, before anyone presses a pull (ADR 0033).
/// </para>
/// </summary>
public class OddsFeedStatus(IOptions<IngestionOptions> options, SourceRegistry registry)
{
    private readonly IngestionOptions _options = options.Value;

    public IReadOnlyList<OddsProviderState> Describe()
    {
        var live = registry.OddsSources.Select(s => s.Key).ToHashSet();

        // A provider that is disabled in configuration is not a feed with a problem; it is a
        // feed the operator chose not to run, and it has no business on the health surface.
        // One enabled without a key is a misconfiguration and stays listed, with the reason.
        var states = new List<OddsProviderState>
        {
            Describe("odds-api-io", "odds-api.io", _options.OddsApiIo, live,
                "100 requests/hour, 500/day. One slate is two requests whatever the book list.", []),

            Describe("the-odds-api", "The Odds API", _options.TheOddsApi, live,
                "500 credits/month, billed as markets x regions per call.",
                TheOddsApiBooks.Notes(_options.TheOddsApi.EffectiveBookmakers))
        };

        // The second source (ADR 0033) is listed only where its key is held: it has no Enabled
        // switch to be misconfigured, so without a key there is nothing to explain.
        if (_options.SportsGameOdds.HasKey)
        {
            var sgo = _options.SportsGameOdds;
            states.Add(new OddsProviderState(
                Key: SportsGameOddsAdapter.SourceKey,
                Name: "SportsGameOdds",
                IsLive: registry.SportsGameOdds is not null,
                Enabled: true,
                Books: sgo.RequestedBooks.Select(SportsGameOddsAdapter.CanonicalBook).ToList(),
                Reason: registry.SportsGameOdds is not null
                    ? $"{sgo.MonthlyObjects:N0} objects a rolling {sgo.BudgetWindow.TotalDays:N0} days, one per game returned; "
                      + $"{sgo.ObjectReserve:N0} held back. Pulled on request only (Pull Caesars — league). "
                      + "Not yet verified against the live service."
                    : "Key held but not registered; check the startup log.",
                Notes: []));
        }

        return states.Where(s => s.IsLive || s.Enabled).ToList();
    }

    private static OddsProviderState Describe(
        string key, string name, SourceOptions config, IReadOnlySet<string> live, string tier, IReadOnlyList<string> notes)
    {
        var hasKey = !string.IsNullOrWhiteSpace(config.ApiKey);

        var reason = live.Contains(key) ? tier
            : !config.Enabled ? "Disabled. Set Enabled=true in the Ingestion section."
            : !hasKey ? "Enabled, but no API key. Enter one below, or supply it in configuration; never in the repo."
            : "Configured but not registered; check the startup log.";

        return new OddsProviderState(
            Key: key,
            Name: name,
            IsLive: live.Contains(key),
            Enabled: config.Enabled,
            Books: config.EffectiveBookmakers,
            Reason: notes.Count == 0 ? reason : reason + " " + string.Join(" ", notes),
            Notes: notes);
    }
}

/// <param name="Notes">
/// What the configured book list will not buy, in plain sentences — a paid-only book, a list past
/// ten (<see cref="TheOddsApiBooks.Notes"/>). Already appended to <paramref name="Reason"/>; here
/// apart for a window that would rather draw them as their own lines.
/// </param>
public record OddsProviderState(
    string Key,
    string Name,
    bool IsLive,
    bool Enabled,
    IReadOnlyList<string> Books,
    string Reason,
    IReadOnlyList<string>? Notes = null);
