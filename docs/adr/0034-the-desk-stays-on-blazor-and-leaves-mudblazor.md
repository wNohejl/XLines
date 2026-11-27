# ADR 0034 — The Desk stays on Blazor and leaves MudBlazor

**Status:** Accepted as a trial, 2026-11-27. Every step sits behind a switch that is off by default
(Decision §3); the ADR is settled, one way or the other, when the owner has compared the two desks.

**Builds on:** [ADR 0004](0004-global-interactive-server-render-mode.md) (global Interactive Server,
because MudBlazor does not support static SSR), [ADR 0008](0008-gloss-as-affordance-and-the-mudblazor-seam.md)
(the MudBlazor seam) and [ADR 0016](0016-apple-feel-design-system.md) (Apple's HIG as the design system).

**Amends:** ADR 0008. The seam stays exactly as it is while the switch is off. With the switch on, a
primitive draws without MudBlazor, and nothing in `mud-bridge.css` applies to it.

## Context

The Desk is reusable now: `XLines.Desk` is a Razor Class Library with no domain in it, packed for
NuGet with a `dotnet new deskapp` template. The owner asked whether, with that done, a JavaScript
front end would be the better direction. Three readings were taken on 2026-11-27.

**The Desk as it stands.** 79 `.razor` files and about 9k lines of C# in the Desk, 62 `.razor` in
XLines.Web, about 15k lines of token-driven CSS, about 2,100 bUnit tests, and about 2.5k lines of
plain ES modules with no bundler. MudBlazor is almost gone already: the Desk hand-draws its menus,
popovers, pop-ups, tabs, switches, fields, toasts, alerts, the command palette and the window
manager. What is left of MudBlazor in markup is `MudButton` (inside `DeskButton`), `MudTooltip`
(`DeskTooltip`), `MudDialog` (`DeskSheet`), `MudChart` (`DeskChart`), `MudDataGrid` (`DeskGrid`),
`MudIcon`, three providers in `MainLayout`, and `MudChip` inside a `DeskChip` that was being added
the day this was written. It also remains in the public surface: `ButtonType`, Mud icon strings, and
`MudTheme` built in C# by `DeskTheme`.

**The JavaScript side.** Its real advantage for a dense desk is a mature headless layer (Base UI 1.x,
TanStack Table v9). The Desk has already built that layer for itself. A rewrite would keep the CSS
and the rules and discard the Razor, the C# and the tests. And Blazor components do not travel to a
JavaScript site: Blazor custom elements take no child content, are not properly supported on
`blazor.web.js`, and bring a circuit or the WebAssembly runtime onto the host page
(<https://learn.microsoft.com/en-us/aspnet/core/blazor/components/js-spa-frameworks>).

**The platform.** Much of what a component library was for is now Baseline in the browser: the
Popover API and `<dialog>`, invoker commands (December 2025), CSS anchor positioning (January 2026),
same-document view transitions (October 2025), scroll-driven animations (October 2026). Not yet:
cross-document view transitions and the customizable `<select>`, both without Firefox. .NET 11
(November 2026) gave `Virtualize` variable row heights and `ScrollToItemAsync`, and QuickGrid sorting
and paging that work without interactivity.

**The HIG.** Read live, it agrees with what the Desk already enforces (chrome floats over content,
hairlines over shadows, one accent, type carries hierarchy). It has no web page at all, and says
nothing about data grids, URLs, breakpoints, toasts or live data, so those stay the Desk's own rules.
Three places where the live HIG and the Desk's skills differ are recorded under Consequences.

So the thing that limits where the Desk can go is not Blazor. It is MudBlazor: it is the stated
reason for ADR 0004, for `mud-bridge.css`, for `:root:root`, and for 122 `.mud-*` selectors in
`xlines.css`.

## Decision

### 1. The Desk stays on Blazor

No JavaScript framework, no bundler, no npm. JavaScript stays what it is: small ES modules for what a
server round trip would make laggy (a drag, a measured fold), and it shrinks where the platform now
does the job.

A JavaScript stack is reconsidered only for a site that must be public, high-traffic and not .NET.
That site would take the tokens and the CSS, not the components.

### 2. MudBlazor is retired, one primitive at a time, cheapest first

| Step | Primitive | Replaced by | State |
|---|---|---|---|
| 1 | `DeskButton` (`MudButton`) | a `<button>`; `.desk-btn` already draws all of it | native path built 2026-11-27 behind `DeskNative.Button`; the owner has not chosen |
| 2 | `DeskTooltip` (`MudTooltip`) | a `popover` element, CSS anchor positioning (Baseline January 2026), `@starting-style`, and `js/tooltip.js` | native path built 2026-11-27 behind `DeskNative.Tooltip`; the owner has not chosen |
| 3 | `DeskChip` (`MudChip`) | a `<button>` that is the capsule itself, dressed by `.desk-chip--native` | native path built 2026-11-27 behind `DeskNative.Chip`; the owner has not chosen |
| 4 | `DeskSheet` (`MudDialog`), `MudDialogProvider` | `<dialog>` | not begun |
| 5 | `MudIcon`, `Glyph` | inline SVG from the Desk's own set | not begun |
| 6 | `DeskChart` (`MudChart`) | SVG in Razor, as `DeskSparkline` and `FieldGraphic` already are | not begun |
| 7 | `DeskGrid` (`MudDataGrid`) | a table over `Virtualize`; the expensive one | not begun |
| 8 | `MudThemeProvider`, `MudPopoverProvider`, `DeskTheme`'s `MudTheme`, `mud-bridge.css`, the package reference | nothing | not begun |

Only after step 8 can ADR 0004 be revisited: static SSR, per-page interactivity or WebAssembly become
choices then, and not before.

The platform features in Context are used as each step reaches them, and only once Baseline.
`backdrop-filter` stays where ADR 0016 put it: on things that float, with a solid fallback.

### 3. Each step is a switch, off by default, and the old path stays until the owner chooses

`DeskNative` says which primitives draw without MudBlazor. `AddDesk` registers `DeskNative.Off`
unless the host passes another; XLines.Web reads `Desk:Native` from configuration. With it unset the
site is byte for byte the one that was there before.

To compare, run a second desk beside the first with the switch on (and the scheduler off, as any
second instance must be):

```powershell
$env:Desk__Native='true'; $env:Ingestion__HostScheduler='false'; $env:Reliability__HostEvaluator='false'
dotnet run --project src/XLines.Web --launch-profile http --urls http://localhost:5290
```

To revert: leave the switch off. Nothing else is needed, and nothing is lost by waiting. To revert
for good, `git revert` the step's commit; each step is one commit that touches only its primitive,
its tests and this table. When the owner chooses a step's native path, the Mud path and that step's
flag are deleted in one commit, and the row above says so.

### 4. The tokens are the portable asset

The Desk's custom properties are exported to the Design Tokens format (DTCG 2025.10) when a second
stack first needs them, and not before. Nothing is built for a consumer that does not exist.

## The checklist

A step is done when every line holds. Lines marked *(switch on)* are checked with `Desk:Native=true`.

1. With the switch off, the rendered markup of the primitive is unchanged, and the full suite is green.
2. *(switch on)* The primitive renders no element with a `mud-` class and no MudBlazor component.
3. *(switch on)* Every public parameter still does what its documentation says: the same classes, the
   same `aria-` attributes, the same callbacks, the same focus behaviour.
4. *(switch on)* The existing bUnit tests for the primitive pass in both modes; a test that could only
   pass under MudBlazor is rewritten to state the behaviour, not the library.
5. *(switch on)* No new `!important`, no literal colour, size or duration: every value is a Desk token.
6. *(switch on)* Keyboard: reachable by Tab, operated by Enter and Space, a visible focus ring;
   `prefers-reduced-motion` honoured.
7. *(switch on)* Any platform feature used is Baseline, and the ADR table names it.
8. No domain in the Desk (`DeskVocabularyTests`), and no change to `mud-bridge.css` for a native path.
9. `dotnet build XLines.slnx` clean; `dotnet test XLines.slnx` green.
10. Seen in a browser in both themes, switch off beside switch on, before the Mud path is deleted.

## Consequences

- Two paths live in each primitive under trial. That is the price of a revert that costs nothing, and
  it is paid one primitive at a time, not across the Desk.
- **Step 1 as measured** (two desks side by side, Ops window, dark theme): every key has the same
  box, glyph position, ink, fill, shadow and focus ring in both. Two things were MudBlazor's and
  had to be said in the Desk: a press does not travel to a pressable parent, and a disabled key's
  ink is the tertiary label. One difference is kept on purpose: MudBlazor gave a disabled button
  `pointer-events: none`, so its `not-allowed` cursor and its title never showed; the native key
  takes the pointer, shows both, and still refuses the press.
- **Step 2 as measured** (same two desks). The native tooltip's body is in the page from the start,
  as a closed popover beside its trigger, and one delegated script raises it: no call to the server
  to show a tooltip, and no listener per tooltip. The wrapper lays its trigger out as MudBlazor's
  did; the surface, type, shadow, gap, pause, hover, keyboard focus and Escape are the same. Three
  differences are kept on purpose:
  - **It has a measure.** MudBlazor's positioner wrote `max-width: none` inline, so a plain-text
    tooltip ran in one line across the window (935 px in the Ops window). The native one wraps at
    the 20rem every structured tooltip already had.
  - **It has no arrow.** The side and the gap say what it belongs to, as a Mac's help tag does, and
    an arrow cannot follow a tooltip that CSS turns to the other side.
  - **It goes away on a press or a scroll**, not only when the pointer leaves.

  The application's stylesheets named `.mud-tooltip-root` in eight selectors; each now reads
  `:is(.mud-tooltip-root, .desk-tipped)`, which weighs the same. `js/tooltip.js` is loaded only on a
  desk whose tooltips are native. Not seen in a browser: the structured shape, and the turn to the
  other side at a screen edge.
- **Step 3 as measured** (same two desks, the Parts bin, dark theme). The native chip is one
  `<button>` where MudBlazor drew a button holding a content span; the fill, ink, pressed weight,
  focus ring, `aria-expanded`, `aria-controls` and `aria-pressed` are the same, Space and Enter
  press it, and a press keeps focus on it. A press reaches a pressable parent on both desks alike.
  Two differences:
  - **Its size is the one the chip was written to have.** `mud-bridge.css` sets the chip's
    footnote type and 22px key on `.mud-chip`, but MudBlazor's `.mud-chip.mud-chip-size-medium`
    (32px tall, 14px type) is one class heavier and wins, so the MudBlazor chip on screen is 32px
    and 14px. The native chip is 22px and the footnote size, as `DeskChip`'s own notes and that
    rule say it should be. This was a defect in the seam, found by the comparison. It was fixed
    in the seam the same day, in its own commit and not as part of this step
    (`.desk-chip.mud-chip-text` now states the height and the type size itself), so on a desk
    built after that commit the two chips are the same size.
  - **Disabled is the button's own `disabled`.** MudBlazor drew a disabled chip as a `<div>` with
    `tabindex="-1"`, `aria-disabled` and `pointer-events: none`. The native one is a disabled
    button: out of the tab order by the browser, and, as with the native key, it takes the pointer
    so its `not-allowed` cursor and its title show.

  The native block copies five literal figures from the bridge's chip rule (22px, 3px, 14px, 999px
  and the 50% dim); the Desk has no token for them yet.
- The public surface still names MudBlazor (`ButtonType`, icon strings) until step 5 and step 8. A
  consumer of the package sees no change until then.
- **The package.** `XLines.Desk.csproj` declares 0.5.0 and the Desk has moved since `desk-v0.5.0`
  (two commits, and the chip then in flight). The `desk-v*` tags exist only locally: this repository
  (ADR 0031) has never published a Desk package. `AddDesk` gains an optional parameter, so the next
  version is 0.6.0. Publishing it is a tag push the owner makes.
- **Where the live HIG and the Desk's skills differ** (read 2026-11-27):
  - macOS controls are 28 pt by default and 20 pt at the least; larger targets are for touch only
    (Designing for macOS, 2026-10-28). The skills say 44.
  - macOS type is 13 pt by default and 10 pt at the least, and every text style has an emphasized
    weight (Typography, 2025-12-16).
  - The HIG says to avoid an app-specific appearance setting (Dark Mode). The Desk offers themes and
    accents by ADR 0030, and that stays: it is a deliberate divergence, named here.
- Liquid Glass is not adopted beyond ADR 0016's materials. Reviews measured its contrast near 1.5:1
  in places against 4.5:1, and refraction needs an SVG `backdrop-filter` that only Chromium draws.
- Not sourced when this was written: that Linear, Vercel and Stripe set the convention for dense web
  desks, and that React with shadcn and TanStack is "the default". Neither bears on the decision.
