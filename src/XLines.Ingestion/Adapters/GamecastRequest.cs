namespace XLines.Ingestion.Adapters;

/// <summary>
/// The gamecast feed's reads, marked as such for the ESPN port beneath them.
///
/// <para>
/// A feed read is not like the port's other requests. Its next poll is fifteen seconds away and
/// is its retry, so it is asked once and given <see cref="Timeout"/> — not the standard resilience
/// handler's three retries of ten seconds each, which would hold the poll, and the watched game
/// with it, for most of a minute on one bad summary. And a read for a game a window is watching
/// (<see cref="Watched"/>) goes ahead of every other request waiting at <see cref="EspnPacer"/>.
/// </para>
///
/// <para>
/// Ambient, for the length of a <c>using</c>, rather than a parameter on
/// <see cref="IGamecastSource"/>: the port's three reads, its fakes and the backport would all
/// grow an argument that only the feed sets and only the adapter's one request method reads.
/// <see cref="AsyncLocal{T}"/> carries it down the awaits to that method and no further: it
/// does not leak back to the caller, and a request outside the <c>using</c> is an ordinary one.
/// </para>
/// </summary>
public sealed class GamecastRequest
{
    private static readonly AsyncLocal<GamecastRequest?> Ambient = new();

    /// <summary>
    /// Set on a request the resilience handler must not retry (<c>AddProviderClient</c>). Only a
    /// gamecast read carries it; every other request is retried as before.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("xlines.no-retry");

    private GamecastRequest(bool watched, TimeSpan timeout)
    {
        Watched = watched;
        Timeout = timeout;
    }

    /// <summary>A window is watching the game: its requests go first at the pacer.</summary>
    public bool Watched { get; }

    /// <summary>How long one request may take, headers and body, before it is given up on.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The read in progress on this flow, or null for an ordinary request.</summary>
    public static GamecastRequest? Current => Ambient.Value;

    /// <summary>Marks every ESPN request until the returned scope is disposed as one gamecast read's.</summary>
    public static IDisposable Begin(bool watched, TimeSpan timeout)
    {
        var scope = new Scope(Ambient.Value);
        Ambient.Value = new GamecastRequest(watched, timeout);
        return scope;
    }

    private sealed class Scope(GamecastRequest? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
