using XLines.Core.Entities;

namespace XLines.Core.Analytics;

/// <summary>
/// One game from a team's perspective, with what the market said about it.
///
/// <para>
/// The two result fields are null when there is no reading — no closing line, or a game not
/// yet final. Null is distinct from <see cref="EntryResult.Push"/>: a game with no stored
/// number did not land on it.
/// </para>
/// </summary>
public record TeamGameLogRow(
    int GameId,
    DateTimeOffset StartsAt,
    string Opponent,
    bool Home,
    int? ScoreFor,
    int? ScoreAgainst,
    GameStatus Status,
    decimal? ClosingSpread,
    EntryResult? AtsResult,
    decimal? ClosingTotal,
    EntryResult? TotalResult,
    // The sport and the opponent's abbreviation, so a log can draw the opponent's crest.
    string? SportKey = null,
    string? OpponentAbbrev = null)
{
    /// <summary>Null until the game is final — a game in progress has no result yet.</summary>
    public bool? Won => Status == GameStatus.Final && ScoreFor is { } f && ScoreAgainst is { } a
        ? f > a
        : null;

    /// <summary>Whether this row contributed any market data, for coverage counting.</summary>
    public bool HasClosingLine => ClosingSpread is not null || ClosingTotal is not null;

    /// <summary>
    /// Whether the score means anything yet.
    ///
    /// A fixture that has not been played carries zeroes rather than nulls, so a null check
    /// alone renders an unplayed game as a nil-nil draw — the exact confusion ADR 0003 is
    /// about. The status is what distinguishes them, so it has to be part of the question.
    /// </summary>
    public bool HasScore
        => Status is GameStatus.Final or GameStatus.Live
           && ScoreFor is not null
           && ScoreAgainst is not null;
}
