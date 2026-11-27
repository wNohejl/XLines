using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using XLines.Desk.Primitives;

namespace XLines.Desk.Tests;

/// <summary>
/// ADR 0034, step 1: the key without MudBlazor. Off, a button is the MudButton it was. On, it is
/// a <c>&lt;button&gt;</c> that keeps every promise <see cref="DeskButtonTests"/> checks: the same
/// classes, the same attributes, the same refusals.
/// </summary>
public class DeskButtonNativeTests : DeskTestContext
{
    private void Native() => Services.AddSingleton(DeskNative.All);

    [Fact]
    public void A_desk_that_says_nothing_draws_the_mud_button()
    {
        var cut = RenderComponent<DeskButton>(p => p.AddChildContent("Go"));

        Assert.Contains("mud-button-root", cut.Find("button").ClassList);
        Assert.DoesNotContain("desk-btn--native", cut.Find("button").ClassList);
    }

    [Fact]
    public void Switched_on_nothing_of_mudblazor_is_drawn()
    {
        Native();

        var cut = RenderComponent<DeskButton>(p => p
            .Add(x => x.Icon, "<path d=\"M0 0\"/>")
            .AddChildContent("Go"));

        Assert.DoesNotContain("mud-", cut.Markup);
        Assert.Contains("desk-btn--native", cut.Find("button").ClassList);
    }

    /// <summary>The class list is the contract with desk.css, and it is the same contract.</summary>
    [Theory]
    [InlineData(DeskEmphasis.Plain, DeskRole.Normal, DeskKeySize.Medium, false)]
    [InlineData(DeskEmphasis.Tinted, DeskRole.Normal, DeskKeySize.Small, false)]
    [InlineData(DeskEmphasis.Filled, DeskRole.Destructive, DeskKeySize.Large, true)]
    public void The_desk_classes_are_the_ones_the_mud_button_carried(
        DeskEmphasis emphasis, DeskRole role, DeskKeySize size, bool busy)
    {
        IEnumerable<string> Desk(IRenderedComponent<DeskButton> cut)
            => cut.Find("button").ClassList.Where(c => c.StartsWith("desk-btn") && c != "desk-btn--native");

        void Set(ComponentParameterCollectionBuilder<DeskButton> p) => p
            .Add(x => x.Emphasis, emphasis)
            .Add(x => x.Role, role)
            .Add(x => x.Size, size)
            .Add(x => x.Busy, busy)
            .Add(x => x.Class, "mine")
            .AddChildContent("Go");

        var mud = Desk(RenderComponent<DeskButton>(Set)).ToArray();

        using var on = new NativeBench();
        var native = Desk(on.RenderComponent<DeskButton>(Set)).ToArray();

        Assert.Equal(mud, native);
    }

    [Fact]
    public void An_icon_alone_is_a_square_with_a_name()
    {
        Native();

        var cut = RenderComponent<DeskButton>(p => p
            .Add(x => x.Icon, "<path d=\"M0 0\"/>")
            .Add(x => x.Title, "Refresh"));

        var button = cut.Find("button");

        Assert.Contains("desk-btn--icon", button.ClassList);
        Assert.Equal("Refresh", button.GetAttribute("title"));
        Assert.NotNull(button.QuerySelector("svg[aria-hidden='true']"));
    }

    [Theory]
    [InlineData(MudBlazor.ButtonType.Button, "button")]
    [InlineData(MudBlazor.ButtonType.Submit, "submit")]
    [InlineData(MudBlazor.ButtonType.Reset, "reset")]
    public void The_type_is_the_one_asked_for(MudBlazor.ButtonType type, string expected)
    {
        Native();

        var cut = RenderComponent<DeskButton>(p => p.Add(x => x.ButtonType, type).AddChildContent("Go"));

        Assert.Equal(expected, cut.Find("button").GetAttribute("type"));
    }

    [Fact]
    public void A_press_reaches_the_caller()
    {
        Native();
        var presses = 0;

        var cut = RenderComponent<DeskButton>(p => p
            .Add(x => x.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, () => presses++))
            .AddChildContent("Go"));

        cut.Find("button").Click();

        Assert.Equal(1, presses);
    }

    [Fact]
    public void Busy_refuses_the_press_and_keeps_its_tab_stop()
    {
        Native();
        var presses = 0;

        var cut = RenderComponent<DeskButton>(p => p
            .Add(x => x.Busy, true)
            .Add(x => x.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, () => presses++))
            .AddChildContent("Going"));

        var button = cut.Find("button");
        button.Click();

        Assert.Equal(0, presses);
        Assert.Equal("true", button.GetAttribute("aria-busy"));
        Assert.False(button.HasAttribute("disabled"));
    }

    [Fact]
    public void Disabled_is_the_elements_own_disabled()
    {
        Native();

        var cut = RenderComponent<DeskButton>(p => p.Add(x => x.Disabled, true).AddChildContent("Go"));

        Assert.True(cut.Find("button").HasAttribute("disabled"));
    }

    /// <summary>A caller may add an attribute; it may not take the type away.</summary>
    [Fact]
    public void A_callers_attributes_are_kept_and_cannot_unmake_the_key()
    {
        Native();

        var cut = RenderComponent<DeskButton>(p => p
            .AddUnmatched("data-key", "refresh")
            .AddUnmatched("aria-label", "Refresh the board")
            .AddUnmatched("type", "reset")
            .AddChildContent("Go"));

        var button = cut.Find("button");

        Assert.Equal("refresh", button.GetAttribute("data-key"));
        Assert.Equal("Refresh the board", button.GetAttribute("aria-label"));
        Assert.Contains("desk-btn", button.ClassList);
        Assert.Equal("button", button.GetAttribute("type"));
    }

    /// <summary>
    /// A press inside a pressable parent: whatever the MudButton did, the native key does. Stated
    /// as a comparison so the test holds either way and fails only if the two part company.
    /// </summary>
    [Fact]
    public void A_press_travels_to_a_pressable_parent_exactly_as_far_as_it_did()
    {
        int Presses(TestContext bench)
        {
            var parent = 0;
            var cut = bench.Render(b =>
            {
                b.OpenElement(0, "div");
                b.AddAttribute(1, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => parent++));
                b.OpenComponent<DeskButton>(2);
                b.AddAttribute(3, nameof(DeskButton.ChildContent), (RenderFragment)(c => c.AddContent(0, "Go")));
                b.CloseComponent();
                b.CloseElement();
            });

            cut.Find("button").Click();
            return parent;
        }

        using var on = new NativeBench();

        Assert.Equal(Presses(this), Presses(on));
    }

    private sealed class NativeBench : DeskTestContext
    {
        public NativeBench() => Services.AddSingleton(DeskNative.All);
    }
}
