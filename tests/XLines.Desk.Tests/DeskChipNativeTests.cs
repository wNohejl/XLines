using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using XLines.Desk.Primitives;
using XLines.Desk.Tests.Support;

namespace XLines.Desk.Tests;

/// <summary>
/// ADR 0034, step 3: the chip without MudBlazor. Off, it is the MudChip it was (DeskChipTests).
/// On, the capsule is a <c>&lt;button&gt;</c> with the same classes, the same state told to a
/// reader and the same callbacks. Each test here states what DeskChipTests states of the MudChip,
/// as behaviour: where that one names how MudBlazor draws a disabled chip, this one names what a
/// disabled key is.
/// </summary>
public class DeskChipNativeTests : DeskTestContext
{
    public DeskChipNativeTests() => Services.AddSingleton(DeskNative.All);

    private IRenderedComponent<DeskChip> Chip(
        bool pressed = false,
        EventCallback<bool> changed = default,
        string? controls = null,
        bool disclosure = false,
        bool disabled = false)
        => RenderComponent<DeskChip>(p =>
        {
            p.Add(x => x.Pressed, pressed)
             .Add(x => x.PressedChanged, changed)
             .Add(x => x.Disclosure, disclosure)
             .Add(x => x.Disabled, disabled)
             .AddChildContent("More · 5 items");
            if (controls is not null)
                p.Add(x => x.Controls, controls);
        });

    [Fact]
    public void Nothing_of_mudblazor_is_drawn()
    {
        Assert.DoesNotContain("mud-", Chip().Markup);
        Assert.DoesNotContain("mud-", Chip(pressed: true, controls: "rows-1").Markup);
        Assert.DoesNotContain("mud-", Chip(disabled: true).Markup);
        Assert.DoesNotContain("mud-", RenderComponent<DeskChip>(p => p
            .Add(x => x.Icon, MudBlazor.Icons.Material.Filled.ExpandMore)
            .AddChildContent("More")).Markup);
    }

    /// <summary>The capsule is the button: one element, its words directly inside it.</summary>
    [Fact]
    public void Is_one_button_that_is_the_capsule()
    {
        var cut = Chip();
        var chip = cut.Find(".desk-chip");

        Assert.Equal("BUTTON", chip.TagName);
        Assert.Equal("button", chip.GetAttribute("type"));
        Assert.Contains("desk-chip--native", chip.ClassList);
        Assert.Equal(0, chip.ChildElementCount);
        Assert.Equal("More · 5 items", chip.TextContent.Trim());
        Assert.False(chip.HasAttribute("tabindex"));
        Assert.Equal("BUTTON", cut.Nodes.OfType<AngleSharp.Dom.IElement>().Single().TagName);
    }

    [Fact]
    public void A_disclosure_reports_expanded_and_what_it_controls()
    {
        var closed = Chip(controls: "rows-1").Find(".desk-chip");

        Assert.Equal("false", closed.GetAttribute("aria-expanded"));
        Assert.Equal("rows-1", closed.GetAttribute("aria-controls"));
        Assert.False(closed.HasAttribute("aria-pressed"));

        Assert.Equal("true", Chip(pressed: true, controls: "rows-1").Find(".desk-chip").GetAttribute("aria-expanded"));
    }

    /// <summary>A region with no id is still a disclosure when the caller says so.</summary>
    [Fact]
    public void A_disclosure_without_an_id_reports_expanded_alone()
    {
        var chip = Chip(pressed: true, disclosure: true).Find(".desk-chip");

        Assert.Equal("true", chip.GetAttribute("aria-expanded"));
        Assert.False(chip.HasAttribute("aria-controls"));
        Assert.False(chip.HasAttribute("aria-pressed"));
    }

    [Fact]
    public void A_toggle_reports_pressed()
    {
        Assert.Equal("false", Chip().Find(".desk-chip").GetAttribute("aria-pressed"));
        Assert.Equal("true", Chip(pressed: true).Find(".desk-chip").GetAttribute("aria-pressed"));
        Assert.False(Chip().Find(".desk-chip").HasAttribute("aria-expanded"));
    }

    [Fact]
    public void Pressed_is_drawn_by_a_class()
    {
        Assert.Contains("desk-chip--pressed", Chip(pressed: true).Find(".desk-chip").ClassList);
        Assert.DoesNotContain("desk-chip--pressed", Chip().Find(".desk-chip").ClassList);
    }

    [Fact]
    public void A_click_asks_for_the_other_state()
    {
        var asked = new List<bool>();

        Chip(changed: EventCallback.Factory.Create<bool>(this, asked.Add)).Find(".desk-chip").Click();
        Chip(pressed: true, changed: EventCallback.Factory.Create<bool>(this, asked.Add)).Find(".desk-chip").Click();

        Assert.Equal([true, false], asked);
    }

    /// <summary>The caller that wants the event gets it, before the state it asks for.</summary>
    [Fact]
    public void A_click_raises_the_event_and_then_the_state()
    {
        var order = new List<string>();

        RenderComponent<DeskChip>(p => p
            .Add(x => x.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, _ => order.Add("click")))
            .Add(x => x.PressedChanged, EventCallback.Factory.Create<bool>(this, _ => order.Add("state")))
            .AddChildContent("More")).Find(".desk-chip").Click();

        Assert.Equal(["click", "state"], order);
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

        cut.Find(".desk-chip").Click();

        Assert.Equal("true", cut.Find(".desk-chip").GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll("button"));
    }

    /// <summary>
    /// A disabled key is the element's own <c>disabled</c>: the browser takes it out of the tab
    /// order and tells a reader, and the chip refuses a press that reaches it anyway.
    /// </summary>
    [Fact]
    public void Disabled_is_the_button_s_own_and_ignores_the_press()
    {
        var asked = new List<bool>();

        var chip = Chip(changed: EventCallback.Factory.Create<bool>(this, asked.Add), disabled: true).Find(".desk-chip");

        Assert.True(chip.HasAttribute("disabled"));
        Assert.Contains("desk-chip--disabled", chip.ClassList);
        chip.Click();
        Assert.Empty(asked);

        Assert.False(Chip().Find(".desk-chip").HasAttribute("disabled"));
    }

    [Fact]
    public void An_icon_leads_the_words_and_is_hidden_from_a_reader()
    {
        var chip = RenderComponent<DeskChip>(p => p
            .Add(x => x.Icon, MudBlazor.Icons.Material.Filled.ExpandMore)
            .AddChildContent("More")).Find(".desk-chip");

        var svg = chip.QuerySelector("svg");
        Assert.NotNull(svg);
        Assert.Equal(chip.FirstElementChild, svg);
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

        Assert.Contains("list__more", chip.ClassList);
        Assert.Equal("list__more", chip.ClassList.Last());
        Assert.Equal("Shows the rest", chip.GetAttribute("title"));
        Assert.Equal("more", chip.GetAttribute("data-testid"));
    }

    /// <summary>A caller cannot turn the key into a submit button or strip its classes by accident.</summary>
    [Fact]
    public void An_extra_attribute_does_not_replace_the_type()
    {
        var chip = RenderComponent<DeskChip>(p => p
            .AddUnmatched("type", "submit")
            .AddChildContent("More")).Find(".desk-chip");

        Assert.Equal("button", chip.GetAttribute("type"));
    }

    // ---- Beside the MudChip ----------------------------------------------------------------

    [Fact]
    public void A_desk_that_says_nothing_draws_the_mud_chip()
    {
        using var mud = new MudBench();

        var cut = mud.RenderComponent<DeskChip>(p => p.AddChildContent("More"));

        Assert.NotNull(cut.Find(".mud-chip.desk-chip"));
        Assert.Empty(cut.FindAll(".desk-chip--native"));
    }

    /// <summary>
    /// A press on a chip inside something pressable reaches that parent or does not, and it is
    /// the same answer on both desks.
    /// </summary>
    [Fact]
    public void A_press_travels_to_a_parent_as_the_mud_chip_s_does()
    {
        using var mud = new MudBench();

        Assert.Equal(ParentHeard(mud), ParentHeard(this));
    }

    private static bool ParentHeard(TestContext bench)
    {
        var heard = false;
        var cut = bench.Render(b =>
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "onclick", EventCallback.Factory.Create<MouseEventArgs>(bench, _ => heard = true));
            b.OpenComponent<DeskChip>(2);
            b.AddAttribute(3, nameof(DeskChip.ChildContent), (RenderFragment)(c => c.AddContent(0, "More")));
            b.CloseComponent();
            b.CloseElement();
        });

        cut.Find(".desk-chip").Click();
        return heard;
    }

    /// <summary>The stylesheet dresses the native chip by its own class, and the bridge is not asked to.</summary>
    [Fact]
    public void The_stylesheet_dresses_the_native_class_and_the_bridge_does_not()
    {
        var css = Path.Combine(Repo.Root(), "src", "XLines.Desk", "wwwroot", "css");

        Assert.Contains(".desk-chip--native {", DeskCss.Sheet("desk.css"));
        Assert.DoesNotContain("desk-chip--native", File.ReadAllText(Path.Combine(css, "mud-bridge.css")));
    }

    private sealed class MudBench : DeskTestContext;
}
