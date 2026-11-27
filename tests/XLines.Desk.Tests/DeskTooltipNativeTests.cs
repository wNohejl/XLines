using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using XLines.Desk.Layout;
using XLines.Desk.Primitives;
using XLines.Desk.Tests.Support;

namespace XLines.Desk.Tests;

/// <summary>
/// ADR 0034, step 2: the tooltip without MudBlazor. Off, it is the MudTooltip it was. On, the body
/// is a popover element beside its trigger: on the page from the start, out of sight and out of
/// the accessibility tree until the Desk's script raises it, and placed by the stylesheet. So what
/// a test can state is the markup that script and that stylesheet read.
/// </summary>
public class DeskTooltipNativeTests : DeskTestContext
{
    public DeskTooltipNativeTests() => Services.AddSingleton(DeskNative.All);

    private static readonly DeskTooltipRow[] Regions =
    [
        new("North", "+185", "−1.2% margin", Emphasis: true),
        new("South", "+181", "−2.6% margin"),
    ];

    private IRenderedComponent<DeskTooltip> Tip(
        string text = "Counts every open item.",
        DeskTooltipPlacement placement = DeskTooltipPlacement.Top)
        => RenderComponent<DeskTooltip>(p => p
            .Add(x => x.Text, text)
            .Add(x => x.Placement, placement)
            .AddChildContent(b =>
            {
                b.OpenElement(0, "button");
                b.AddAttribute(1, "type", "button");
                b.AddAttribute(2, "aria-label", "Open items");
                b.AddContent(3, "12");
                b.CloseElement();
            }));

    private IRenderedComponent<DeskTooltip> Structured(string text = "")
        => RenderComponent<DeskTooltip>(p => p
            .Add(x => x.Title, "Best reading")
            .Add(x => x.Caption, "Taken an hour ago.\nAgainst the reference.")
            .Add(x => x.Rows, Regions)
            .Add(x => x.Footnote, "+3 more in Details")
            .Add(x => x.Text, text)
            .AddChildContent("12"));

    [Fact]
    public void Nothing_of_mudblazor_is_drawn()
    {
        Assert.DoesNotContain("mud-", Tip().Markup);
        Assert.DoesNotContain("mud-", Structured().Markup);
    }

    /// <summary>The wrapper holds the trigger first and the body after it, and nothing else.</summary>
    [Fact]
    public void The_trigger_and_its_tooltip_share_one_wrapper()
    {
        var wrapper = Tip().Find(".desk-tipped");

        Assert.Equal(2, wrapper.ChildElementCount);
        Assert.Equal("BUTTON", wrapper.FirstElementChild!.TagName);
        Assert.Contains("desk-tooltip", wrapper.LastElementChild!.ClassList);
    }

    /// <summary>
    /// A manual popover: raised and lowered by the Desk's script alone, so a press elsewhere or a
    /// second popover does not decide when an explanation goes away.
    /// </summary>
    [Fact]
    public void The_body_is_a_manual_popover_that_says_it_is_a_tooltip()
    {
        var body = Tip().Find(".desk-tipped > .desk-tooltip");

        Assert.Equal("manual", body.GetAttribute("popover"));
        Assert.Equal("tooltip", body.GetAttribute("role"));
        Assert.Equal("Counts every open item.", body.TextContent.Trim());
    }

    [Fact]
    public void The_trigger_keeps_its_own_accessible_name()
    {
        var button = Tip().Find("button");

        Assert.Equal("Open items", button.GetAttribute("aria-label"));
        Assert.Null(button.GetAttribute("title"));
    }

    [Theory]
    [InlineData(DeskTooltipPlacement.Top, "top")]
    [InlineData(DeskTooltipPlacement.Bottom, "bottom")]
    [InlineData(DeskTooltipPlacement.Start, "start")]
    [InlineData(DeskTooltipPlacement.End, "end")]
    public void The_side_is_named_for_the_stylesheet(DeskTooltipPlacement placement, string expected)
    {
        Assert.Equal(expected, Tip(placement: placement).Find(".desk-tipped").GetAttribute("data-desk-tip"));
    }

    [Fact]
    public void Above_is_the_default_side()
    {
        var cut = RenderComponent<DeskTooltip>(p => p.Add(x => x.Text, "A note"));

        Assert.Equal("top", cut.Find(".desk-tipped").GetAttribute("data-desk-tip"));
    }

    /// <summary>The structured shape is the same markup it was inside MudBlazor's popover.</summary>
    [Fact]
    public void The_structured_shape_draws_its_parts()
    {
        var cut = Structured();

        Assert.Equal("Best reading", cut.Find(".desk-tooltip > .desk-tip .desk-tip__title").TextContent);
        Assert.Equal("Taken an hour ago.\nAgainst the reference.", cut.Find(".desk-tip__caption").TextContent);
        Assert.Equal("+3 more in Details", cut.Find(".desk-tip__footnote").TextContent);

        var rows = cut.FindAll(".desk-tip__row");
        Assert.Equal(2, rows.Count);
        Assert.Contains("desk-tip__row--emphasis", rows[0].ClassList);
        Assert.Equal("+185", rows[0].QuerySelector(".desk-tip__value")!.TextContent);
    }

    [Fact]
    public void The_structured_shape_is_drawn_for_the_eye_and_spoken_as_a_sentence()
    {
        var composed = Structured();
        Assert.Equal("true", composed.Find(".desk-tip__face").GetAttribute("aria-hidden"));
        Assert.Equal(composed.Instance.Spoken, composed.Find(".desk-tip > .sr-only").TextContent);

        var written = Structured("North leads at +185.");
        Assert.Equal("North leads at +185.", written.Find(".desk-tip > .sr-only").TextContent);
    }

    // ---- The script and the stylesheet the markup is written for ------------------------------

    [Fact]
    public void A_native_desk_loads_the_tooltip_script_and_a_mud_desk_does_not()
    {
        Assert.Contains("js/tooltip.js", RenderComponent<DeskScripts>().Markup);

        using var mud = new MudBench();
        Assert.DoesNotContain("tooltip.js", mud.RenderComponent<DeskScripts>().Markup);
    }

    [Fact]
    public void A_desk_that_says_nothing_draws_the_mud_tooltip()
    {
        using var mud = new MudBench();

        var cut = mud.RenderComponent<DeskTooltip>(p => p.Add(x => x.Text, "A note").AddChildContent("12"));

        Assert.NotNull(cut.Find(".mud-tooltip-root"));
        Assert.Empty(cut.FindAll(".desk-tipped"));
    }

    /// <summary>
    /// The script and the stylesheet agree on three names, and the markup above is the third
    /// party. Read from the files, so renaming one side without the other fails here.
    /// </summary>
    [Fact]
    public void The_script_and_the_stylesheet_use_the_markup_s_names()
    {
        var script = File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Desk", "wwwroot", "js", "tooltip.js"));
        var css = File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Desk", "wwwroot", "css", "controls.css"));

        Assert.Contains("[data-desk-tip]", script);
        Assert.Contains(".desk-tooltip", script);
        Assert.Contains("'--desk-tip'", script);

        Assert.Contains("position-anchor: --desk-tip;", css);
        foreach (var side in new[] { "bottom", "start", "end" })
            Assert.Contains($"[data-desk-tip=\"{side}\"] > .desk-tooltip", css);
    }

    private sealed class MudBench : DeskTestContext;
}
