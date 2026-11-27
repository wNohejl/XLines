using Bunit;
using XLines.Desk.Primitives;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace XLines.Desk.Tests;

/// <summary>
/// The chip: a small capsule key that folds or filters what it sits beside. It wraps MudChip and
/// is dressed by the Desk; it is a native button a reader hears with its state, so Enter and
/// Space are the browser's own, and its state is the caller's, shown by fill and weight rather
/// than hue.
/// </summary>
public class DeskChipTests : DeskTestContext
{
    private IRenderedComponent<DeskChip> Chip(
        bool pressed = false,
        EventCallback<bool> changed = default,
        string? controls = null,
        bool disabled = false)
        => RenderComponent<DeskChip>(p =>
        {
            p.Add(x => x.Pressed, pressed)
             .Add(x => x.PressedChanged, changed)
             .Add(x => x.Disabled, disabled)
             .AddChildContent("More · 5 items");
            if (controls is not null)
                p.Add(x => x.Controls, controls);
        });

    [Fact]
    public void Is_a_mud_chip_dressed_by_the_desk()
    {
        var chip = Chip().Find(".mud-chip");

        Assert.Contains("desk-chip", chip.GetAttribute("class"));
        Assert.Equal("More · 5 items", chip.TextContent.Trim());
    }

    /// <summary>
    /// A native button, so the tab stop, Enter and Space are the browser's own: nothing in the chip
    /// re-implements a key, and a form around it is never submitted by it.
    /// </summary>
    [Fact]
    public void Is_a_native_button()
    {
        var chip = Chip().Find(".desk-chip");

        Assert.Equal("BUTTON", chip.TagName);
        Assert.Equal("button", chip.GetAttribute("type"));
        Assert.NotEqual("-1", chip.GetAttribute("tabindex"));
    }

    /// <summary>Given the region it controls, it is a disclosure: expanded or not, never "pressed".</summary>
    [Fact]
    public void A_disclosure_reports_expanded_and_what_it_controls()
    {
        var closed = Chip(controls: "rows-1").Find(".desk-chip");

        Assert.Equal("false", closed.GetAttribute("aria-expanded"));
        Assert.Equal("rows-1", closed.GetAttribute("aria-controls"));
        Assert.False(closed.HasAttribute("aria-pressed"));

        Assert.Equal("true", Chip(pressed: true, controls: "rows-1").Find(".desk-chip").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void A_toggle_reports_pressed()
    {
        Assert.Equal("false", Chip().Find(".desk-chip").GetAttribute("aria-pressed"));
        Assert.Equal("true", Chip(pressed: true).Find(".desk-chip").GetAttribute("aria-pressed"));
        Assert.False(Chip().Find(".desk-chip").HasAttribute("aria-expanded"));
    }

    /// <summary>Pressed is a class the sheet draws by fill and weight; the chip names no colour.</summary>
    [Fact]
    public void Pressed_is_drawn_by_a_class_not_a_colour()
    {
        var chip = Chip(pressed: true).Find(".desk-chip");

        Assert.Contains("desk-chip--pressed", chip.GetAttribute("class"));
        Assert.DoesNotContain("mud-chip-color-primary", chip.GetAttribute("class"));
        Assert.DoesNotContain("desk-chip--pressed", Chip().Find(".desk-chip").GetAttribute("class"));
    }

    [Fact]
    public void A_click_asks_for_the_other_state()
    {
        var asked = new List<bool>();

        Chip(changed: EventCallback.Factory.Create<bool>(this, asked.Add)).Find(".desk-chip").Click();
        Chip(pressed: true, changed: EventCallback.Factory.Create<bool>(this, asked.Add)).Find(".desk-chip").Click();

        Assert.Equal([true, false], asked);
    }

    /// <summary>The same element in both states, so focus stays on the chip a press re-renders.</summary>
    [Fact]
    public void A_press_keeps_the_same_element()
    {
        var pressed = false;
        IRenderedComponent<DeskChip>? cut = null;
        cut = RenderComponent<DeskChip>(p => p
            .Add(x => x.Pressed, pressed)
            .Add(x => x.Controls, "rows-1")
            .Add(x => x.PressedChanged, EventCallback.Factory.Create<bool>(this, v =>
            {
                pressed = v;
                cut!.SetParametersAndRender(q => q.Add(x => x.Pressed, pressed));
            }))
            .AddChildContent("More"));

        var before = cut.Find(".desk-chip");
        before.Click();
        var after = cut.Find(".desk-chip");

        Assert.Equal("true", after.GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll(".desk-chip"));
    }

    [Fact]
    public void Disabled_is_out_of_the_tab_order_and_ignores_the_press()
    {
        var asked = new List<bool>();

        var chip = Chip(changed: EventCallback.Factory.Create<bool>(this, asked.Add), disabled: true).Find(".desk-chip");

        // Mud draws a disabled chip as an inert element: out of the tab order, no click handler.
        Assert.Equal("-1", chip.GetAttribute("tabindex"));
        Assert.Equal("true", chip.GetAttribute("aria-disabled"));
        Assert.DoesNotContain("blazor:onclick", chip.OuterHtml);
        Assert.Empty(asked);
    }

    /// <summary>A fold's chevron is drawn bare before the words and hidden from a reader, who hears the state instead.</summary>
    [Fact]
    public void An_icon_leads_the_words_and_is_hidden_from_a_reader()
    {
        var chip = RenderComponent<DeskChip>(p => p
            .Add(x => x.Icon, MudBlazor.Icons.Material.Filled.ExpandMore)
            .AddChildContent("More")).Find(".desk-chip");

        var svg = chip.QuerySelector("svg");
        Assert.NotNull(svg);
        Assert.Equal("true", svg!.GetAttribute("aria-hidden"));
        Assert.Equal("More", chip.TextContent.Trim());
        Assert.Null(Chip().Find(".desk-chip").QuerySelector("svg"));
    }

    [Fact]
    public void A_class_title_and_extra_attributes_reach_the_chip()
    {
        var chip = RenderComponent<DeskChip>(p => p
            .Add(x => x.Class, "list__more")
            .Add(x => x.Title, "Shows the rest")
            .AddUnmatched("data-testid", "more")
            .AddChildContent("More")).Find(".desk-chip");

        Assert.Contains("list__more", chip.GetAttribute("class"));
        Assert.Equal("Shows the rest", chip.GetAttribute("title"));
        Assert.Equal("more", chip.GetAttribute("data-testid"));
    }
}
