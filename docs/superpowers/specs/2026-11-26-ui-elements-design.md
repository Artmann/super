# UI elements, input and the Storybook

Date: 2026-11-26

`Super.Ui` has the SwiftUI layout core (stacks, frames, boxes, text, scroll views, drag and
drop) but few controls, one hard-coded look, mouse only. This makes it a full toolkit: a
neutral theme that fits most games, the SwiftUI control set plus a few game pieces, focus
for keyboard and gamepad, touch, popups, animation, and a Storybook example that shows it
all and doubles as the test bed.

## Decisions

- **Neutral theme.** Near-black zinc greys, one accent, soft shadows, 6-8 px corners, like
  shadcn's default. A dark and a light theme ship; a game changes tokens with `with`.
- **Mouse, touch, keyboard and gamepad** all drive every control.
- **Built from primitives.** Every control is a component made of public views and
  modifiers, so a game can build its own when the stock one doesn't fit.
- **Testable by meaning.** Controls describe themselves (role, label, value, checked,
  focused, disabled), so a test asks "is the music toggle on?" instead of reading pixels.

## Layers

### Environment

`TextStyle`, which already flows down the tree, becomes `UiEnvironment`: the text style,
the `Theme`, whether the subtree is disabled, and the control size. A component reads it
in `Body` through `Environment` (a protected property on `UiView`). A change in the
environment already reruns a node's Body, because the node compares it.

Modifiers: `.Theme(theme)`, `.Disabled(bool)`, `.ControlSize(size)`, `.TextStyle(style)`,
`.Italic()`, `.Weight(...)`, `.TextAlign(...)`.

### Drawing

The overlay quad gets a `Kind` and four more numbers, still one draw call:

| Kind | Used for |
| --- | --- |
| Box | Rounded rectangles with a radius per corner, border, gradient fill |
| Ellipse | Circles and ellipses, filled or stroked |
| Arc | Spinners, radial gauges, cooldowns |
| Segment | Capsule lines between two points, for the built-in icons (check, chevron, cross, plus, minus, menu, arrows) |
| Shadow | A blurred rounded rectangle under a box |

Gradients are linear (angle) or radial, two stops, on any box. Text and images stay as
they are.

### Input

Mouse and touch are pointers. Keyboard and gamepad move focus.

- **Pointers.** `UiPointer` already handles the mouse. Touch adds `Input.Touches` and
  touch events with ids. A tap clicks, a drag in a scroll view scrolls with momentum, a
  long press opens a context menu, and touch never hovers. GLFW has no touch, so on
  Windows the window is subclassed to read `WM_POINTER` touch messages; elsewhere touch
  comes only from scripts and tests until a backend gives it.
- **Focus.** A view made `.Focusable()` can hold focus. Tab and Shift+Tab walk the order,
  arrows and the gamepad's d-pad or left stick move spatially, Enter/Space/South press,
  Escape/East goes back or closes. Focus starts empty, so a game's keys aren't taken until
  the player uses navigation keys on an interface that has something focusable, or a view
  asks with `.DefaultFocus()`. While something holds focus, the keys it uses don't reach
  the game. The focus ring only shows after keyboard or gamepad use, like `:focus-visible`.
- **Text.** A focused `TextField` takes `Input.Typed` and editing keys: caret, selection,
  Home/End, Ctrl+A/C/V/X, Backspace/Delete, Enter submits.

### Layers above the tree

Popups live in the tree where they're declared, like SwiftUI's `.sheet`: a modifier names
the content and whether it shows. During layout they register with the tree, which lays
them out above everything, anchored to their owner or centred with a scrim. The top modal
takes the pointer and traps focus; a click outside a light popup closes it. Toasts stack
in a corner and time out.

### Animation

UI time is its own clock, advanced per frame, and set by hand in tests. `UseAnimation(target,
duration, easing)` returns the value moving toward the target and keeps the component
running until it arrives. Controls use it for knobs, thumbs, fades and expanding sections.
`.Transition(...)` fades a view in when it appears, or fades it in as it rises or drops into place.

### Semantics

`.Semantics(role, label, value, ...)` records what a view means. Every control sets it.
`UiTree.Elements` and `run.Element(id)` read it back in tests, with frame, focus and
state.

## Elements

Done when each has stories in the Storybook and tests through mouse, touch, keyboard and
gamepad where it applies.

- Text styles and `Label`, `Divider`, `Badge`, `KeyHint`, `Kbd`
- Shapes: `Rectangle`, `RoundedRectangle`, `Capsule`, `Circle`, `Ellipse`, with `.Fill`,
  `.Stroke`; `.Shadow`, gradients, `.ClipShape`
- Layout: `.Overlay(view)`, `.Background(view)`, `.Offset`, `.Hidden`, `.LayoutPriority`,
  `.AspectRatio`, `Grid` + `GridRow`, `FlowStack`, `ViewThatFits`, `LazyVStack`,
  `.SafeAreaPadding`
- Controls: button styles and sizes, `Toggle` (switch, checkbox), `Slider`, `Stepper`,
  `Picker` (segmented, menu, radio), `TextField`, `SecureField`, `Menu`, context menu,
  `ProgressView` (bar, spinner), `Gauge`, `Link`, `ColorPicker`
- Containers: `Section`/`Form`, `Card`, `DisclosureGroup`, `TabView`, `NavigationStack`
- Popups: `Sheet`, `Alert`, `ConfirmationDialog`, `Popover`, `Toast`

## Storybook

`examples/Storybook`: a sidebar of elements, stories for each, a theme switch, and five
showcase screens (settings, inventory, main menu, HUD, character creator). Stories live in
a registry of plain functions; `--story button/disabled` opens one, so `--screenshot` can
shoot any story. A test renders every story headlessly and checks for overflow, cut text
that isn't meant to be cut, and clashing ids.

## Order

1. Environment, theme, text styles, `Disabled`; quad kinds, shadows, gradients, shapes,
   icons; `Divider`, `Badge`, `Label`. Storybook shell. Done.
2. Button styles, `Toggle`, `Slider`, `Stepper`, segmented `Picker`, `ProgressView`,
   `Gauge`, semantics. Done, with a radio `Picker` too. The indeterminate spinner and the
   switch's moving knob need the UI clock, so they moved to step 7.
3. Focus, keyboard and gamepad navigation; `TextField`, `SecureField`. Done. Fields are one
   line; a multi-line `TextEditor` and selecting text with the mouse are left for later.
4. Touch. Done on Windows through `WM_POINTER`, tried only with scripts so far. A long
   press runs `.OnLongPress`, and opens a context menu. No pinch, and a finger can't drag
   and drop yet.
5. Popups: `Menu`, context menu, menu `Picker`, `Sheet`, `Alert`, `Popover`, `Toast`. Done,
   with a confirmation dialog too. No submenus, no key that opens a context menu, and toasts
   have no buttons.
6. Containers and layout, in two parts. The layout part is done: overlay, background view,
   offset, hidden, layout priority, aspect ratio, `Grid`/`GridRow`, `FlowStack`,
   `ViewThatFits` and `LazyVStack`. A lazy stack's rows share one height, and`.SafeAreaPadding`
   waits until the window knows its safe area. The containers part is done too: `Card`,
   `Section`/`Form`, `LabeledContent`, `DisclosureGroup`, `TabView` and `NavigationStack`,
   with Escape and East going back and the bumpers switching tabs. Going back forgets the
   page underneath's state.
7. Animation and transitions, including the spinning `ProgressView` and the switch's and
   slider's moving knobs; showcase screens; polish passes. Done: `UseAnimation`, `UseTime`,
   `.Transition(Entrance)` with fade, rise and drop, popups and their scrims easing in, and
   `.Animations(false)`. The five showcases are stories. Views don't animate out, and there's
   no slide or scale transition yet.
