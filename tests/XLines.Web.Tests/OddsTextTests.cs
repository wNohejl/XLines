using XLines.Core.Analytics;
using XLines.Web.Components;

namespace XLines.Web.Tests;

/// <summary>
/// A price, a line and a CLV reading, printed one way everywhere. The Game result snippet kept a
/// copy of its own that printed a scaled journal decimal as "-3.00"; the panels' copy did not.
/// </summary>
public class OddsTextTests
{
    [Theory]
    [InlineData(145, "+145")]
    [InlineData(-110, "-110")]
    [InlineData(0, "0")]
    public void An_american_price_carries_its_sign(int american, string expected)
        => Assert.Equal(expected, OddsText.Price(american));

    [Fact]
    public void A_price_taken_to_the_cent_is_the_core_format()
    {
        Assert.Equal(OddsMath.FormatPrice(104.35m), OddsText.Price(104.35m));
        Assert.Equal(OddsMath.FormatPrice(-110m), OddsText.Price(-110m));
        Assert.Equal(OddsMath.FormatPrice(0m), OddsText.Price(0m));
    }

    [Theory]
    [InlineData("-3", "-3")]
    [InlineData("4.5", "+4.5")]
    [InlineData("-1.5", "-1.5")]
    [InlineData("0", "0")]
    public void A_line_prints_as_a_book_prints_it(string value, string expected)
        => Assert.Equal(expected, OddsText.Line(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void A_scaled_decimal_line_prints_without_its_trailing_zeros()
    {
        // As the journal's numeric column reads it back.
        Assert.Equal("-3", OddsText.Line(-3.00m));
        Assert.Equal("+2.5", OddsText.Line(2.50m));
        Assert.Equal("0", OddsText.Line(0.00m));
        Assert.Equal("+0.25", OddsText.Line(0.250m));
    }


    [Fact]
    public void Clv_at_the_same_number_is_cents_in_percent()
    {
        var clv = new ClvResult(-105m, -120);

        Assert.True(clv.SameNumber);
        Assert.Equal(clv.CentsPercent.ToString("+0.0;-0.0") + "%", OddsText.Clv(clv));
        Assert.StartsWith("+", OddsText.Clv(clv));
    }

    [Theory]
    [InlineData("1.00", "+1 pts")]
    [InlineData("-1.50", "-1.5 pts")]
    public void Clv_where_the_line_moved_is_points(string points, string expected)
    {
        var clv = new ClvResult(-110m, -110, decimal.Parse(points, System.Globalization.CultureInfo.InvariantCulture));

        Assert.False(clv.SameNumber);
        Assert.Equal(expected, OddsText.Clv(clv));
    }
}
