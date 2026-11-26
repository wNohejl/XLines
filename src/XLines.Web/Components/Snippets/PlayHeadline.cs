using System.Globalization;
using System.Text.RegularExpressions;
using XLines.Data.Gamecast;

namespace XLines.Web.Components.Snippets;

/// <summary>
/// The short label a play's row leads with — "16-yard pass", "6-yard sack", "Incomplete pass",
/// "Punt, 48 yards", "Touchdown, 31-yard pass" — so the log can be scanned down its first words
/// the way NFL.com's Game Center is, with ESPN's sentence left beneath it for the detail.
///
/// <para>
/// It is read from the play's <see cref="GamecastPlay.Type"/> (ESPN's type text) first, its
/// flags and <see cref="GamecastPlay.Yardage"/> second, and its sentence only where the type and
/// the number cannot say it, each time by a pattern the sentence prints the same way in every
/// game we hold. Every rule was written against the stored NFL plays rather than ESPN's
/// documentation, because the stored plays disagree with what a reader would expect in three
/// places worth knowing:
/// </para>
/// <list type="bullet">
/// <item><see cref="GamecastPlay.Yardage"/> is the net the offence gained, penalty included: a
/// 29-yard scramble with holding enforced behind it is stored as −3. So a play that drew a flag
/// takes its own yards from the sentence ("for 29 yards"), and a play the flag wiped out ("No
/// Play", "NULLIFIED") is headed as the penalty it became.</item>
/// <item>On a punt or a kickoff it is the return, not the kick: the punt's distance is the
/// sentence's "punts 48 yards", and a kickoff's figure is named a return.</item>
/// <item>Kneels are stored as runs and spikes as incomplete passes, and a handful of scoring
/// summaries ("Eric Saubert 13 Yd pass from Sam Darnold") arrive under a fumble's type; each is
/// told apart by its sentence or its flag rather than headed as what the type says.</item>
/// </list>
/// <para>
/// Sentence case and the desk's own voice throughout, a compound distance hyphenated ("6-yard
/// sack") and a plain one not ("Punt, 48 yards"). Null when nothing useful can be said — no type,
/// or one this table does not know — and then the row draws no headline at all rather than a
/// guess.
/// </para>
/// </summary>
public static partial class PlayHeadline
{
    /// <summary>The play's headline, or null when there is nothing worth heading it with.</summary>
    public static string? For(GamecastPlay play)
    {
        ArgumentNullException.ThrowIfNull(play);

        var type = play.Type?.Trim();
        if (string.IsNullOrEmpty(type))
            return null;

        var text = play.Text ?? string.Empty;

        // A flag that wiped the play out is what happened: "Field goal" over a kick the offside
        // erased, or "Touchdown" over a return the hold called back, would be a headline the
        // score contradicts. A play that scored anyway keeps its score.
        if (play.IsPenalty && !play.IsScoring && (NoPlay().IsMatch(text) || Nullified().IsMatch(text)))
            return Penalty(text);

        // Two points to the defence: a sack in the end zone, grounding enforced there, a hold
        // behind the goal line. ESPN types the sack "Safety", the other two as the foul or the pass.
        if (play.IsScoring && !type.Contains("Touchdown", StringComparison.OrdinalIgnoreCase) && SafetyWord().IsMatch(text))
            return "Safety";

        switch (type.ToLowerInvariant())
        {
            case "rush":
            {
                if (Kneel().IsMatch(text))
                    return "Kneel";

                var yards = OwnYards(play, text);
                var what = Scramble().IsMatch(text) ? "scramble" : "run";

                return yards switch
                {
                    null => what == "run" ? "Run" : "Scramble",
                    > 0 => $"{Yd(yards.Value)} {what}",
                    0 when what == "run" => "No gain",
                    0 => "Scramble, no gain",
                    _ when what == "run" => $"{Yd(-yards.Value)} loss",
                    _ => $"Scramble, {Yd(-yards.Value)} loss"
                };
            }

            case "pass reception":
            {
                var yards = OwnYards(play, text);
                return yards switch
                {
                    null => "Pass complete",
                    > 0 => $"{Yd(yards.Value)} pass",
                    0 => "Pass, no gain",
                    _ => $"Pass, {Yd(-yards.Value)} loss"
                };
            }

            case "pass incompletion":
                if (Spike().IsMatch(text))
                    return "Spike";

                // Grounding is a loss the passer took, not a throw that fell.
                return play.IsPenalty && Grounding().IsMatch(text) ? Penalty(text) : "Incomplete pass";

            case "sack":
                return OwnYards(play, text) is < 0 and var lost ? $"{Yd(-lost)} sack" : "Sack";

            case "passing touchdown":
                return Touchdown(OwnYards(play, text), "pass");

            case "rushing touchdown":
                return Touchdown(OwnYards(play, text), "run");

            case "pass interception return":
                return play.IsScoring ? "Touchdown, interception return" : "Interception";

            case "interception return touchdown":
                return play.IsScoring ? Touchdown(Return(play), "interception return") : "Interception";

            case "fumble return touchdown":
                return Touchdown(Return(play), "fumble return");

            case "punt return touchdown":
                return Touchdown(Return(play), "punt return");

            case "kickoff return touchdown":
                return Touchdown(Return(play), "kickoff return");

            case "blocked punt touchdown":
                return "Touchdown, blocked punt";

            case "blocked field goal touchdown":
                return "Touchdown, blocked field goal";

            case "fumble recovery (opponent)":
                return "Fumble lost";

            case "sack opp fumble recovery":
                return play.IsScoring ? "Touchdown, fumble return" : "Sack, fumble lost";

            // Two of these are scoring summaries filed under the wrong type; the score is what they say.
            case "fumble recovery (own)":
                return play.IsScoring ? "Touchdown" : "Fumble, recovered";

            case "field goal good":
                return Distance("Field goal", Kick(play, text));

            case "field goal missed":
                return Distance("Missed field goal", Kick(play, text));

            // A blocked kick's Yardage is the recovery's, so the distance comes from the sentence alone.
            case "blocked field goal":
                return Distance("Blocked field goal", Number(FieldGoalDistance(), text));

            case "punt":
                return Distance("Punt", Number(PuntDistance(), text));

            case "blocked punt":
                return "Blocked punt";

            case "muffed punt recovery (opponent)":
                return "Muffed punt";

            case "kickoff":
                if (Onside().IsMatch(text))
                    return "Onside kick";
                if (Touchback().IsMatch(text))
                    return "Kickoff, touchback";
                return !play.IsPenalty && play.Yardage is > 0 and var returned
                    ? $"Kickoff, {Yd(returned)} return"
                    : "Kickoff";

            case "penalty":
                return Penalty(text);

            case "safety":
                return "Safety";

            case "defensive 2pt conversion":
                return "Defensive two-point conversion";

            case "timeout":
                return TimeoutBy().Match(text) is { Success: true } m ? $"Timeout, {m.Groups["team"].Value}" : "Timeout";

            case "official timeout":
                return "Official timeout";

            case "two-minute warning":
                return "Two-minute warning";

            case "end period":
                return "End of quarter";

            case "end of half":
                return "End of half";

            case "end of regulation":
                return "End of regulation";

            case "end of game":
                return "End of game";

            default:
                return null;
        }
    }

    /// <summary>
    /// "Penalty, defensive holding, 5 yards" from the first foul the sentence enforces; "Penalty"
    /// when none reads cleanly (offsetting, declined, or one enforced with no distance stated).
    /// </summary>
    private static string Penalty(string text)
    {
        if (Foul().Match(text) is not { Success: true } m)
            return "Penalty";

        var foul = m.Groups["foul"].Value.Trim().ToLower(CultureInfo.InvariantCulture);
        var yards = int.Parse(m.Groups["yards"].Value, CultureInfo.InvariantCulture);

        return foul.Length == 0 ? "Penalty" : $"Penalty, {foul}, {Yards(yards)}";
    }

    /// <summary>
    /// The yards the play itself made: <see cref="GamecastPlay.Yardage"/> where no flag touched it,
    /// otherwise the sentence's first "for 8 yards" or "for no gain", since the stored figure then
    /// carries the penalty too. Null when a flagged play's sentence states none.
    /// </summary>
    private static int? OwnYards(GamecastPlay play, string text)
    {
        if (!play.IsPenalty)
            return play.Yardage;

        if (ForYards().Match(text) is not { Success: true } m)
            return null;

        return m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>A return's length, trusted only when it is positive and no flag moved it.</summary>
    private static int? Return(GamecastPlay play)
        => !play.IsPenalty && play.Yardage is > 0 and var y ? y : null;

    /// <summary>A field goal's distance: the sentence's "38 yard field goal", else the stored figure (which is the distance on a kick no one blocked).</summary>
    private static int? Kick(GamecastPlay play, string text)
        => Number(FieldGoalDistance(), text) ?? (play.Yardage is > 0 and var y ? y : null);

    private static int? Number(Regex pattern, string text)
        => pattern.Match(text) is { Success: true } m ? int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) : null;

    private static string Touchdown(int? yards, string what)
        => yards is > 0 ? $"Touchdown, {Yd(yards.Value)} {what}" : $"Touchdown, {what}";

    private static string Distance(string what, int? yards)
        => yards is > 0 ? $"{what}, {Yards(yards.Value)}" : what;

    /// <summary>The compound form, "6-yard".</summary>
    private static string Yd(int yards) => $"{yards.ToString(CultureInfo.InvariantCulture)}-yard";

    /// <summary>The plain form, "1 yard", "48 yards".</summary>
    private static string Yards(int yards) => yards == 1 ? "1 yard" : $"{yards.ToString(CultureInfo.InvariantCulture)} yards";

    [GeneratedRegex(@"\bNo Play\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoPlay();

    [GeneratedRegex(@"\bNULLIFIED\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Nullified();

    // Case-sensitive on purpose: the sentence shouts the score ("SAFETY") or names the summary
    // ("Team Safety", "Daron Payne Safety"); a lower-case "safety" is a position.
    [GeneratedRegex(@"\b(SAFETY|Safety)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SafetyWord();

    [GeneratedRegex(@"\bPENALTY on [^,]+, (?<foul>[^,.]+?), (?<yards>\d+) yards?, enforced", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Foul();

    [GeneratedRegex(@"\bfor (?:(?<y>-?\d+) yards?|no gain)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForYards();

    [GeneratedRegex(@"\bpunts (?<y>\d+) yards?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PuntDistance();

    [GeneratedRegex(@"\b(?<y>\d+) yard field goal\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FieldGoalDistance();

    [GeneratedRegex(@"\bkneels\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Kneel();

    [GeneratedRegex(@"\bscrambles\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Scramble();

    [GeneratedRegex(@"\bspiked\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Spike();

    [GeneratedRegex(@"\bIntentional Grounding\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Grounding();

    [GeneratedRegex(@"\bkicks onside\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Onside();

    [GeneratedRegex(@"\bTouchback\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Touchback();

    [GeneratedRegex(@"\bTimeout #\d+ by (?<team>[A-Z]{2,4})\b", RegexOptions.CultureInvariant)]
    private static partial Regex TimeoutBy();
}
