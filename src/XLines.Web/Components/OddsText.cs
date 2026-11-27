using XLines.Core.Analytics;

namespace XLines.Web.Components;

/// <summary>
/// A price, a line and a CLV reading as the desk prints them, in one place. Panels, snippets and
/// chips each carried a copy, and one of them printed a journal line as "-3.00".
/// </summary>
public static class OddsText
{
    /// <summary>An American price: "+145", "-110", "0".</summary>
    public static string Price(int american) => american > 0 ? $"+{american}" : american.ToString();

    /// <summary>A price taken to the cent: "+104", "+104.35" (<see cref="OddsMath.FormatPrice(decimal)"/>).</summary>
    public static string Price(decimal american) => OddsMath.FormatPrice(american);

    /// <summary>
    /// A line as a book prints it: "-3", "+4.5". A line read back from the journal carries the
    /// column's scale, and decimal's own ToString printed it as "-3.00".
    /// </summary>
    public static string Line(decimal value) => value.ToString("+0.##;-0.##;0");

    /// <summary>
    /// What a bet gained on the close: cents in percent where it closed at the number taken
    /// ("+2.4%"), points where the line moved ("-1.5 pts").
    /// </summary>
    public static string Clv(ClvResult clv)
        => clv.SameNumber
            ? clv.CentsPercent.ToString("+0.0;-0.0") + "%"
            : clv.PointsGained!.Value.ToString("+0.#;-0.#") + " pts";
}
