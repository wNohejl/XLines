using System.Globalization;
using System.Text.RegularExpressions;
using XLines.Data.CrossReference;
using XLines.Data.Gamecast;

namespace XLines.Web.Components.Snippets;

/// <summary>
/// The numbers behind the Gamecast's field graphic and its team comparison, worked out apart
/// from the markup so a test can pin them without parsing SVG — as <see cref="FieldGeometry"/>
/// does for the strip.
///
/// <para>
/// Every position is in the field graphic's own units: the viewBox is
/// <see cref="FieldGeometry.Width"/> (120) wide and <see cref="Height"/> tall, the away end zone
/// 0–10 on the left, the field 10–110, the home end zone 110–120, and a spot <c>y</c> yards from
/// the <b>home</b> goal line at <c>10 + 100 - y</c> (<see cref="FieldGeometry.X"/>). The home side
/// attacks leftward, toward a higher yard line on the home-goal frame. Every share is 0–100.
/// </para>
///
/// <para>
/// Nothing here is derived from a new read: the comparison is the header, drives, plays and box
/// score the window already holds, and a figure the data does not carry is left out rather than
/// drawn as a zero or split down the middle.
/// </para>
/// </summary>
public static partial class GamecastTelemetry
{
    /// <summary>
    /// The field graphic's height in its viewBox units (its width is <see cref="FieldGeometry.Width"/>).
    /// Taller than the strip of stacked bars it replaced (32), because a thirty-yard pass thrown as
    /// an arc needs the air above the ground to be read as one.
    /// </summary>
    public const double Height = 36;

    /// <summary>
    /// The ground every arc leaves from and lands on, and the ball sits on: low on the field, so
    /// the air above it is the arcs' and the strip under it the losses' dip and the result's name.
    /// </summary>
    public const double Baseline = 27;

    /// <summary>The highest an arc's apex may reach, clear of the yard numbers along the top edge.</summary>
    public const double ArcCeiling = 7;

    /// <summary>The most an arc may rise above <see cref="Baseline"/>.</summary>
    public const double MaxRise = Baseline - ArcCeiling;

    /// <summary>The ground the drive has covered, a quiet strip this thick centred on <see cref="Baseline"/>.</summary>
    public const double BandThickness = 1.2;

    /// <summary>The top of that strip.</summary>
    public const double BandTop = Baseline - BandThickness / 2;

    /// <summary>The vertical middle of the field, where the end zones' abbreviations are set.</summary>
    public const double Middle = Height / 2;

    /// <summary>The fifty's x.</summary>
    public const double Midfield = FieldGeometry.EndZone + 50;

    /// <summary>A five-yard tick's length, in from the top and the bottom edge.</summary>
    public const double TickLength = 1.5;

    /// <summary>Where the bottom edge's ticks start.</summary>
    public const double BottomTickTop = Height - TickLength;

    /// <summary>The baselines of the yard numbers above and below the band.</summary>
    public const double TopNumberY = 4.6;

    /// <inheritdoc cref="TopNumberY"/>
    public const double BottomNumberY = Height - 3.4;

    /// <summary>The ends of the line of scrimmage and the line to gain, just inside the field's edges.</summary>
    public const double LineTop = 0.5;

    /// <inheritdoc cref="LineTop"/>
    public const double LineBottom = Height - 0.5;

    /// <summary>The middle of each end zone, where its side's abbreviation is set.</summary>
    public const double AwayMarkX = FieldGeometry.EndZone / 2;

    /// <inheritdoc cref="AwayMarkX"/>
    public const double HomeMarkX = FieldGeometry.EndZone * 1.5 + 100;

    /// <summary>The covered ground is never drawn narrower than this, so a drive that went nowhere still shows where it was.</summary>
    public const double MinSegment = 0.6;

    // ---- The arcs' shapes -------------------------------------------------------------------
    // An arc's rise is the height of its apex above the ground: a base plus so much a yard, capped.
    // A pass is thrown — it climbs with its length, a forty-yard ball near the ceiling; a run stays
    // on the ground, never more than a few units up whatever it gained; a kick is a pass's height
    // and dotted; a loss dips under the ground by its length, so a sack reads as the ball going
    // back before its colour or its direction is looked at.

    /// <summary>A pass's rise: <see cref="PassRiseBase"/> plus <see cref="PassRisePerYard"/> a yard, at most <see cref="MaxRise"/>.</summary>
    public const double PassRiseBase = 3;

    /// <inheritdoc cref="PassRiseBase"/>
    public const double PassRisePerYard = 0.4;

    /// <summary>A run's (and a penalty's) rise: <see cref="RunRiseBase"/> plus <see cref="RunRisePerYard"/> a yard, at most <see cref="RunRiseMax"/>.</summary>
    public const double RunRiseBase = 0.8;

    /// <inheritdoc cref="RunRiseBase"/>
    public const double RunRisePerYard = 0.1;

    /// <inheritdoc cref="RunRiseBase"/>
    public const double RunRiseMax = 3;

    /// <summary>A kick's rise: <see cref="KickRiseBase"/> plus <see cref="KickRisePerYard"/> a yard, at most <see cref="MaxRise"/>.</summary>
    public const double KickRiseBase = 4;

    /// <inheritdoc cref="KickRiseBase"/>
    public const double KickRisePerYard = 0.25;

    /// <summary>A loss's dip below the ground: <see cref="LossDipBase"/> plus <see cref="LossDipPerYard"/> a yard, at most <see cref="LossDipMax"/>.</summary>
    public const double LossDipBase = 1.2;

    /// <inheritdoc cref="LossDipBase"/>
    public const double LossDipPerYard = 0.2;

    /// <inheritdoc cref="LossDipBase"/>
    public const double LossDipMax = 3.5;

    /// <summary>The dot a play for no gain leaves at its spot, and the latest arc's landing dot.</summary>
    public const double SpotRadius = 0.7;

    /// <summary>The drive's start, a short upright: from this far above the ground…</summary>
    public const double StartMarkAbove = 3;

    /// <summary>…to this far below it.</summary>
    public const double StartMarkBelow = 1.5;

    /// <summary>The ring at the end of a finished drive.</summary>
    public const double EndMarkRadius = 1.1;

    /// <summary>The baseline of the finished drive's result, under the ground and above the bottom yard numbers.</summary>
    public const double ResultY = Baseline + 2.9;

    /// <summary>
    /// The result's centre is kept this far in from either edge, so the longest of ESPN's results
    /// ("Interception Touchdown", about 26 units at its size) can end on a goal line without
    /// running off the field.
    /// </summary>
    public const double ResultInset = 13;

    /// <summary>The ball's arrow, about the ball at x = 0: its back edge's offset, its nose's, and its half-height.</summary>
    public const double ArrowBack = 1.4;

    /// <inheritdoc cref="ArrowBack"/>
    public const double ArrowNose = 4;

    /// <inheritdoc cref="ArrowBack"/>
    public const double ArrowHalf = 1.8;

    /// <summary>The ball's arrow pointing left (home attacking) or right, about x = 0 on <see cref="Baseline"/>.</summary>
    public static string ArrowPoints(bool leftward)
    {
        var s = leftward ? -1 : 1;
        return string.Join(' ',
            Pt(s * ArrowBack, Baseline - ArrowHalf), Pt(s * ArrowNose, Baseline), Pt(s * ArrowBack, Baseline + ArrowHalf));
    }

    /// <summary>Where a finished drive's result is centred: the end's x, kept <see cref="ResultInset"/> in from either edge.</summary>
    public static double ResultX(double endX) => Math.Clamp(endX, ResultInset, FieldGeometry.Width - ResultInset);

    // ---- The field ------------------------------------------------------------------------

    /// <summary>
    /// The yard numbers along the field, at each ten: 10 20 30 40 50 40 30 20 10, left to right,
    /// each at its line's x.
    /// </summary>
    public static IReadOnlyList<YardNumber> YardNumbers { get; } =
        Enumerable.Range(1, 9)
            .Select(i => new YardNumber(FieldGeometry.EndZone + i * 10, i <= 5 ? i * 10 : 100 - i * 10))
            .ToList();

    /// <summary>The x of every ten-yard hairline (the goal lines are the end zones' edges).</summary>
    public static IReadOnlyList<double> TenYardLines { get; } =
        Enumerable.Range(1, 9).Select(i => FieldGeometry.EndZone + i * 10d).ToList();

    /// <summary>The x of every five-yard tick between the ten-yard lines.</summary>
    public static IReadOnlyList<double> FiveYardTicks { get; } =
        Enumerable.Range(0, 10).Select(i => FieldGeometry.EndZone + 5 + i * 10d).ToList();

    /// <summary>
    /// The stretch the drive has covered, from where it started to where the ball is now, on the
    /// field's x; null when either end is unknown. Clamped to the field; never narrower than
    /// <see cref="MinSegment"/>.
    /// </summary>
    public static DriveBand? Band(int? driveStartYardLine, int? yardLine)
    {
        if (driveStartYardLine is not { } start || yardLine is not { } spot)
            return null;

        var a = FieldGeometry.X(Math.Clamp(start, 0, 100));
        var b = FieldGeometry.X(Math.Clamp(spot, 0, 100));

        return new DriveBand(Math.Min(a, b), Math.Max(Math.Abs(a - b), MinSegment));
    }

    /// <summary>
    /// One arc per play of the drive, as ESPN's Gamecast field draws a drive: from each play's
    /// <see cref="GamecastPlay.Start"/> yard line to its <see cref="GamecastPlay.End"/>, on the
    /// ground at <see cref="Baseline"/>, so each lands where the next begins and the drive reads
    /// as the ball going down the field.
    ///
    /// <para>
    /// <paramref name="homeOffence"/> is the side with the ball on this drive: it says which way
    /// is a gain. The play store's yard lines are all on the home-goal frame — the start, the end,
    /// a punt's landing and an interception's return alike (checked against game 2049's 181 plays
    /// and the store's turnovers) — so nothing is flipped per side. Both ends are clamped to the field.
    /// </para>
    ///
    /// <para>
    /// What is left out: a play lacking either end, and the rows that are not the offence moving
    /// the ball — timeouts (ESPN's "Timeout" and "Official Timeout", which repeat the spot, and an
    /// Official Timeout after a punt sits at the <i>next</i> drive's spot under this one), the
    /// two-minute warning, every "End …" of a period, half or game, and every kickoff. A kickoff
    /// opens the receiving side's drive in ESPN's store (its <c>TeamId</c> is the receiver), but
    /// its ball travels from the kicking side's 35 toward the receiver's own goal: drawn, it would
    /// read as the drive's first play losing sixty yards. The drive starts where the return ended,
    /// which is <see cref="GamecastDrive.StartYardLine"/>, and the start mark says so.
    /// </para>
    ///
    /// <para>
    /// A punt or a field goal is a <see cref="DriveArcKind.Kick"/>: drawn, faint and dotted, as
    /// the long ball it was — a punt to where the receiving side took it, a field goal to the goal
    /// line it was kicked at (ESPN ends a good one there and a missed one at its own spot; the
    /// kick went at the posts either way). It is the drive's last word, so it is drawn rather than
    /// left to the result's mark alone, but quietly, so it does not outweigh the drive.
    /// </para>
    ///
    /// <para>
    /// The shape is what the play was: a pass (any play ESPN typed as one, or whose words say
    /// "pass", as a fumble after a catch does) arcs high by its length; anything else that gained
    /// stays low; anything that lost ground dips under it; a penalty is low and dashed and dips
    /// when it went back; a play that went nowhere (an incompletion) is a dot at its spot. A
    /// turnover is drawn as what it was thrown or run as, above the ground whichever way the return
    /// went, and is <see cref="DriveArc.Turnover"/>; a play that scored for the offence is
    /// <see cref="DriveArc.Score"/> (a safety scores for the defence and is not). The last arc is
    /// <see cref="DriveArc.Latest"/>: the play that put the ball where it is, or ended the drive.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DriveArc> DriveArcs(IReadOnlyList<GamecastPlay>? plays, bool homeOffence)
    {
        if (plays is null || plays.Count == 0)
            return [];

        var arcs = new List<DriveArc>();

        foreach (var play in plays)
        {
            if (play.Start?.YardLine is not { } start || play.End?.YardLine is not { } end)
                continue;

            if (IsNotAMove(play.Type))
                continue;

            start = Math.Clamp(start, 0, 100);
            end = Math.Clamp(end, 0, 100);

            var type = play.Type ?? string.Empty;
            var kick = IsKick(type);

            // A field goal goes at the posts: the goal the offence attacks, 100 for home, 0 for away.
            if (kick && Has(type, "Field Goal") && !Has(type, "Blocked"))
                end = homeOffence ? 100 : 0;

            // Home attacks the away goal: a gain takes its yard line up the home-goal frame.
            var gained = homeOffence ? end - start : start - end;
            var length = Math.Abs(end - start);
            var passing = Has(type, "Pass") || Has(type, "Interception") || Has(play.Text, " pass ");

            var kind = kick ? DriveArcKind.Kick
                : play.IsTurnover ? (passing ? DriveArcKind.Pass : DriveArcKind.Run)
                : play.IsPenalty || Has(type, "Penalty") ? DriveArcKind.Penalty
                : gained == 0 ? DriveArcKind.NoGain
                : gained < 0 ? DriveArcKind.Loss
                : passing ? DriveArcKind.Pass
                : DriveArcKind.Run;

            // Height of the apex above the ground; a negative rise is a dip under it.
            var rise = kind switch
            {
                DriveArcKind.Pass => Math.Min(MaxRise, PassRiseBase + PassRisePerYard * length),
                DriveArcKind.Kick => Math.Min(MaxRise, KickRiseBase + KickRisePerYard * length),
                DriveArcKind.Run => Math.Min(RunRiseMax, RunRiseBase + RunRisePerYard * length),
                DriveArcKind.Penalty => (gained < 0 ? -1 : 1) * Math.Min(RunRiseMax, RunRiseBase + RunRisePerYard * length),
                DriveArcKind.Loss => -Math.Min(LossDipMax, LossDipBase + LossDipPerYard * length),
                _ => 0,
            };

            var score = play.IsScoring && !play.IsTurnover && !Has(type, "Safety");

            arcs.Add(Arc(play.Sequence, FieldGeometry.X(start), FieldGeometry.X(end), rise, kind, score, play.IsTurnover));
        }

        if (arcs.Count > 0)
            arcs[^1] = arcs[^1] with { Latest = true };

        return arcs;
    }

    private static DriveArc Arc(long sequence, double from, double to, double rise, DriveArcKind kind, bool score, bool turnover)
    {
        // A quadratic's apex is halfway between its ends' line and its control point, so the
        // control sits twice the rise above the ground.
        var apex = Baseline - rise;
        var control = Baseline - 2 * rise;
        var path = kind == DriveArcKind.NoGain
            ? null
            : $"M {N(from)} {N(Baseline)} Q {N((from + to) / 2)} {N(control)} {N(to)} {N(Baseline)}";

        return new DriveArc(sequence, from, to, apex, kind, score, turnover, Latest: false, path);
    }

    private static bool IsNotAMove(string? type)
        => type is not null
           && (Has(type, "Timeout")
               || Has(type, "Kickoff")
               || Has(type, "Two-minute warning")
               || type.StartsWith("End ", StringComparison.OrdinalIgnoreCase));

    private static bool IsKick(string type) => Has(type, "Punt") || Has(type, "Field Goal");

    private static bool Has(string? text, string part)
        => text is not null && text.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Pt(double x, double y) => $"{N(x)},{N(y)}";

    // ---- The drive chart --------------------------------------------------------------------

    /// <summary>
    /// One drive as the drive chart draws it: a bar from its start yard line to its end, in
    /// percent of the whole field's width (<see cref="FieldGeometry.Width"/>, end zones included,
    /// so a touchdown's end — 0 or 100 on the home-goal frame, as ESPN stores it — lands on the
    /// goal line and not off the row). Null when either end is unknown. A drive that went nowhere
    /// is still <see cref="MinChartBar"/> wide, so it leaves a mark at its spot.
    /// <see cref="DriveBar.Leftward"/> is the way it went: a home drive goes left, an away drive
    /// right, and a drive that lost ground the other way, as its numbers say.
    /// </summary>
    public static DriveBar? ChartBar(int? startYardLine, int? endYardLine, bool homeSide)
    {
        if (startYardLine is not { } start || endYardLine is not { } end)
            return null;

        var a = FieldGeometry.X(Math.Clamp(start, 0, 100));
        var b = FieldGeometry.X(Math.Clamp(end, 0, 100));
        var leftward = b == a ? homeSide : b < a;
        var width = Math.Max(Math.Abs(b - a), MinChartBar);
        var left = Math.Min(Math.Min(a, b), FieldGeometry.Width - width);

        return new DriveBar(Pct(left), Pct(width), Pct(b), leftward);
    }

    /// <summary>The narrowest a drive's bar is drawn, in the field's units (yards).</summary>
    public const double MinChartBar = 1;

    private static double Pct(double x) => Math.Round(100 * x / FieldGeometry.Width, 3);

    /// <summary>
    /// What a drive's <see cref="GamecastDrive.Result"/> was, for the weight of its mark: a
    /// touchdown or a field goal for the offence is a <see cref="DriveOutcome.Score"/>; an
    /// interception, a fumble, a safety, a blocked kick or a punt returned for a score is a
    /// <see cref="DriveOutcome.Turnover"/> — ESPN marks a pick-six <c>IsScore</c>, but the points
    /// were the other side's; anything else (a punt, downs, the clock) is <see cref="DriveOutcome.Other"/>.
    /// </summary>
    public static DriveOutcome Outcome(string? result, bool isScore)
    {
        if (result is null)
            return isScore ? DriveOutcome.Score : DriveOutcome.Other;

        if (Has(result, "Interception") || Has(result, "Fumble") || Has(result, "Safety")
            || Has(result, "Blocked") || Has(result, "Punt Touchdown"))
            return DriveOutcome.Turnover;

        return isScore || Has(result, "Touchdown") || result.Equals("Field Goal", StringComparison.OrdinalIgnoreCase)
            ? DriveOutcome.Score
            : DriveOutcome.Other;
    }

    /// <summary>
    /// A drive's result as a short mark for the drive chart's narrow trailing column — "TD", "FG",
    /// "Punt", "INT", "Downs" — as NFL.com and ESPN abbreviate them; a result this does not know is
    /// kept as ESPN wrote it, and the row's accessible name always says it in full. No result is
    /// "Now" for the drive in progress and nothing otherwise.
    /// </summary>
    public static string? ShortResult(string? result, bool isCurrent)
    {
        if (string.IsNullOrWhiteSpace(result))
            return isCurrent ? "Now" : null;

        return result.Trim() switch
        {
            "Touchdown" => "TD",
            "Field Goal" => "FG",
            "Missed FG" => "FG miss",
            "Interception" => "INT",
            "Interception Touchdown" => "INT TD",
            "Fumble" => "Fumble",
            "Fumble Touchdown" or "Fumble Return Touchdown" => "Fum TD",
            "End of Game" => "End",
            "End of Half" => "Half",
            "Punt Touchdown" => "Punt TD",
            "Blocked Punt Touchdown" or "Blocked FG Touchdown" => "Blk TD",
            var r when r.StartsWith("Blocked FG", StringComparison.OrdinalIgnoreCase) => "Blk FG",
            var r when r.StartsWith("Blocked Punt", StringComparison.OrdinalIgnoreCase) => "Blk punt",
            var r => r,
        };
    }

    // ---- The comparison -------------------------------------------------------------------

    /// <summary>
    /// The two teams side by side, in a fixed order, each row only where the data the window
    /// holds can say it for both sides:
    /// <list type="bullet">
    /// <item>"Time of possession" — each side's drives' <see cref="GamecastDrive.TimeElapsed"/>
    /// ("m:ss") summed; a drive whose time does not read is skipped.</item>
    /// <item>"Total yards" — each side's drives' <see cref="GamecastDrive.Yards"/> summed.</item>
    /// <item>"Passing yards", "Rushing yards" — the box score's <c>passing.YDS</c> and
    /// <c>rushing.YDS</c> (see <see cref="SumStat"/>); only when both sides have a line carrying
    /// the key.</item>
    /// <item>"Drives" and "Scoring drives" — the drives each side has had, and those with
    /// <see cref="GamecastDrive.IsScore"/>.</item>
    /// <item>"Turnovers" — plays with <see cref="GamecastPlay.IsTurnover"/>, charged to the side
    /// that had the ball (<see cref="GamecastPlay.TeamId"/>, else the start's). Lower is better.</item>
    /// </list>
    /// Penalties are not compared: a play says it drew a flag, not on whom, and the side with the
    /// ball is as often the one who gained by it.
    /// </summary>
    public static IReadOnlyList<ComparisonRow> Compare(
        GamecastHeader header,
        IReadOnlyList<GamecastDrive>? drives,
        IReadOnlyList<GamecastPlay>? plays,
        GameBoxScore? box)
    {
        var rows = new List<ComparisonRow>();
        var home = header.Home.Id;
        var away = header.Away.Id;

        var owned = (drives ?? [])
            .Where(d => d.TeamId == home || d.TeamId == away)
            .ToList();

        // Time of possession: seconds, so the shares are of the clock and not of minutes.
        var timed = owned
            .Select(d => (d.TeamId, Seconds: Seconds(d.TimeElapsed)))
            .Where(d => d.Seconds is not null)
            .ToList();

        if (timed.Count > 0)
        {
            var a = timed.Where(d => d.TeamId == away).Sum(d => d.Seconds!.Value);
            var h = timed.Where(d => d.TeamId == home).Sum(d => d.Seconds!.Value);
            rows.Add(Row("Time of possession", Clock(a), Clock(h), a, h));
        }

        var yarded = owned.Where(d => d.Yards is not null).ToList();
        if (yarded.Count > 0)
        {
            var a = yarded.Where(d => d.TeamId == away).Sum(d => d.Yards!.Value);
            var h = yarded.Where(d => d.TeamId == home).Sum(d => d.Yards!.Value);
            rows.Add(Row("Total yards", Int(a), Int(h), a, h));
        }

        if (box is not null)
        {
            foreach (var (label, key) in new[] { ("Passing yards", "passing.YDS"), ("Rushing yards", "rushing.YDS") })
            {
                if (SumStat(box.Away, key) is { } a && SumStat(box.Home, key) is { } h)
                    rows.Add(Row(label, Int(a), Int(h), a, h));
            }
        }

        if (owned.Count > 0)
        {
            var a = owned.Count(d => d.TeamId == away);
            var h = owned.Count(d => d.TeamId == home);
            rows.Add(Row("Drives", Int(a), Int(h), a, h));

            var sa = owned.Count(d => d.TeamId == away && d.IsScore);
            var sh = owned.Count(d => d.TeamId == home && d.IsScore);
            rows.Add(Row("Scoring drives", Int(sa), Int(sh), sa, sh));
        }

        var run = (plays ?? [])
            .Select(p => (Team: p.TeamId ?? p.Start?.TeamId, p.IsTurnover))
            .Where(p => p.Team == home || p.Team == away)
            .ToList();

        if (run.Count > 0)
        {
            var a = run.Count(p => p.Team == away && p.IsTurnover);
            var h = run.Count(p => p.Team == home && p.IsTurnover);
            rows.Add(Row("Turnovers", Int(a), Int(h), a, h, higherIsBetter: false));
        }

        return rows;
    }

    /// <summary>
    /// One side's total of a box-score stat, summed over its players' lines; null when no line
    /// on that side carries the key, or one that does cannot be read as a whole number.
    ///
    /// <para>
    /// The keys are the ESPN parser's (<c>EspnStatsAdapter.ParseBoxScore</c>) as the box-score
    /// read hands them over (<see cref="StatLineKeys.Canonical"/>): a bare <c>YDS</c> is always
    /// qualified with the groups on its line that print it, so <c>passing.YDS</c> is a passer's
    /// yards and <c>rushing.YDS</c> a runner's, whatever else the player did. Team passing yards
    /// here are the sum of every passer's <c>passing.YDS</c> — the yards the passes gained, before
    /// the yards lost to sacks, which ESPN's team line nets off — and rushing yards the sum of
    /// every runner's <c>rushing.YDS</c>.
    /// </para>
    /// </summary>
    public static int? SumStat(IReadOnlyList<BoxScoreLine>? side, string key)
    {
        int? total = null;

        foreach (var line in side ?? [])
        {
            if (!line.Stats.TryGetValue(key, out var raw))
                continue;

            if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                return null;

            total = (total ?? 0) + value;
        }

        return total;
    }

    /// <summary>A drive's "m:ss" as seconds; null when it does not read as one.</summary>
    public static int? Seconds(string? elapsed)
    {
        if (elapsed is null || ClockPattern().Match(elapsed.Trim()) is not { Success: true } m)
            return null;

        return int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
               + int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The away side's share of a pair, 0–100, the home side's the rest; both zero when neither
    /// side has any, rather than an even split nobody earned. A negative figure (a side that has
    /// lost yards) counts as none toward the bar; its number still says what it is.
    /// </summary>
    public static (double Away, double Home) Shares(double away, double home)
    {
        away = Math.Max(away, 0);
        home = Math.Max(home, 0);

        if (away + home <= 0)
            return (0, 0);

        var a = Math.Round(100 * away / (away + home), 1);
        return (a, 100 - a);
    }

    private static ComparisonRow Row(string label, string awayText, string homeText, double away, double home, bool higherIsBetter = true)
    {
        var (a, h) = Shares(away, home);
        return new ComparisonRow(label, awayText, homeText, a, h, higherIsBetter);
    }

    private static string Clock(int seconds)
        => $"{seconds / 60}:{(seconds % 60).ToString("00", CultureInfo.InvariantCulture)}";

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<m>\d{1,3}):(?<s>[0-5]\d)$")]
    private static partial Regex ClockPattern();
}

/// <summary>A yard number on the field, at its line's x.</summary>
public sealed record YardNumber(double X, int Number);

/// <summary>The stretch the drive has covered so far: its left edge and width on the field's x.</summary>
public sealed record DriveBand(double Left, double Width);

/// <summary>The shape one play of the drive is drawn in (<see cref="GamecastTelemetry.DriveArcs"/>).</summary>
public enum DriveArcKind
{
    /// <summary>Thrown: arcs high, by its length.</summary>
    Pass,

    /// <summary>Carried, or anything else that gained: stays low.</summary>
    Run,

    /// <summary>Lost ground (a sack, a run stopped behind the line): dips under the ground.</summary>
    Loss,

    /// <summary>A flag: low and dashed, dipping when it went back.</summary>
    Penalty,

    /// <summary>Went nowhere (an incompletion, a run for none): a dot at the spot, no path.</summary>
    NoGain,

    /// <summary>A punt or a field goal: high, faint and dotted.</summary>
    Kick,
}

/// <summary>
/// One play of the drive on the field. <see cref="From"/> and <see cref="To"/> are where the ball
/// started and ended, on the field's x (so a home gain has <c>To &lt; From</c>); <see cref="Apex"/>
/// is the y the arc reaches (above <see cref="GamecastTelemetry.Baseline"/> for a rise, below it
/// for a dip); <see cref="Path"/> is the SVG quadratic that draws it, null for
/// <see cref="DriveArcKind.NoGain"/>, which is a dot at <see cref="From"/>. <see cref="Score"/> and
/// <see cref="Turnover"/> mark the play that scored for the offence and the one that gave the
/// ball away; <see cref="Latest"/> the drive's last drawn play.
/// </summary>
public sealed record DriveArc(
    long Sequence,
    double From,
    double To,
    double Apex,
    DriveArcKind Kind,
    bool Score,
    bool Turnover,
    bool Latest,
    string? Path);

/// <summary>
/// A drive on the drive chart, in percent of the field's whole width (end zones included):
/// <see cref="Left"/> and <see cref="Width"/> the bar, <see cref="End"/> where it finished, and
/// <see cref="Leftward"/> the way it went.
/// </summary>
public sealed record DriveBar(double Left, double Width, double End, bool Leftward);

/// <summary>What a drive came to, for the weight of its mark on the drive chart.</summary>
public enum DriveOutcome
{
    Other,
    Score,
    Turnover,
}

/// <summary>
/// One line of the team comparison: the label, each side's figure as it is shown, and each
/// side's share of the bar, 0–100 (both zero when neither side has any).
/// <see cref="HigherIsBetter"/> is false for a count a side wants fewer of (turnovers), so
/// nothing reads the longer bar as a lead.
/// </summary>
public sealed record ComparisonRow(
    string Label,
    string AwayText,
    string HomeText,
    double AwayShare,
    double HomeShare,
    bool HigherIsBetter = true);
