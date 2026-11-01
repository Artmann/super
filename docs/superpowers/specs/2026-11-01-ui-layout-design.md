# Interface toolkit: layout, images, scrolling and the mouse

## Why

`Super.Ui` can stack text and fill rectangles, which is enough for a stats panel and
not for a game screen. This work makes it possible to build real game interfaces,
with no text inputs yet.

The test is a crafting screen built only from public `Super.Ui` views. Half-working
game UI is worse than none, so everything visible on that screen works with the mouse
from the first version, and feels immediate.

## Goals

- Layout and styling with the everyday CSS vocabulary: padding per side, margins,
  gaps, grids, min and max sizes, rounded corners, borders, opacity.
- `Image`, `ScrollView`, `ListView`, `Grid`, `Button`, tooltips.
- Full mouse support: hover, press, click, wheel, scrollbar dragging, drag and drop.
- An interface that blocks the game underneath.
- A new example, `examples/Crafting`, that uses all of it.

## Non-goals

- Text fields and form controls. `Input.Typed` stays unused by the interface.
- Keyboard and gamepad navigation, and focus.
- Animations and transitions.
- CSS as a language: no stylesheets, selectors or cascade. These are C# modifiers in
  the existing SwiftUI style. See The box for where their order matters.
- Lists that only build visible rows. `ListView` builds every row and only skips
  drawing those out of view, which suits hundreds of rows, not hundreds of thousands.
- Gradients and shadows. They can come later, and the quad format must not rule them
  out.
- Splitting a stack by dragging. Dragging moves the whole stack.

## Anti-goals

The work has failed, even if the code runs, if any of these are true:

- Hover, scrolling or a dragged item visibly lag behind the cursor.
- A click on a panel also reaches the game behind it.
- A press that starts on one thing and ends on another counts as a click.
- A scrollbar stops following the cursor because the cursor left the bar.
- Quads spill outside a scroll area or are silently dropped by a long list.
- Game code has to read `Input` inside `Ui`, or a seeded run stops repeating.

## The screen

Layout C from the mockups, the "crafting bench":

- Top left, a panel with a scrolling recipe list. Each row has an icon and a name.
  The selected row is highlighted. Clicking a row selects it.
- Top right, the selected recipe: a large icon, name, rarity, description, each
  ingredient with have and need counts, and a Craft button.
- Along the bottom, a strip of inventory slots. Each shows an icon and a stack count.

The Craft button works: it uses up the ingredients and adds the item, and it's
disabled when ingredients are short. Slots show a tooltip with the item's details on
hover. Dragging a slot onto another moves the stack there, merging stacks of the same
item and swapping otherwise. The strip stays one row and doesn't scroll.

Item data lives in JSON through `Content.LoadAll`, as Rooms does. Icons are real
images, not colored squares (see Assets).

The example gets headless tests in `tests/Crafting.Tests` in the style of
`Rooms.Tests`, including clicks, a drag and a scroll driven by `ScriptedInput`, and it
works with `--screenshot` and `--record`.

## Views and modifiers

Names are decided; the exact parameter lists are the builder's.

Modifiers, added to `UiExtensions`:

| Modifier | Meaning |
| --- | --- |
| `.Padding(top, trailing, bottom, leading)` | Per side, next to today's overloads |
| `.Margin(...)` | Same overloads. Space outside the view that backgrounds don't fill |
| `.Frame(minWidth, minHeight, ...)` | Adds minimums to today's size and maximums |
| `.CornerRadius(radius)` | Rounds this view's background, border and image, and clips children |
| `.Border(width, color)` | Drawn inside the view's edge, following its corners |
| `.Opacity(value)` | Multiplies through everything inside the view |
| `.IgnoresPointer()` | The mouse passes through, like CSS `pointer-events: none` |
| `.Tooltip(view)` | Shown near the cursor after a short hover |
| `.OnClick(action)` | See Mouse |
| `.Draggable(payload, preview)` | See Mouse |
| `.DropTarget<T>(onDrop, canDrop)` | See Mouse |

Per-corner radii are not needed.

### The box

The user decided that a chain of modifiers behaves like one CSS box, not like strict
wrapping.

- `.Background`, `.Border`, `.CornerRadius`, `.Opacity` and `.Margin` written next to
  each other, in any order, merge into one box. So `.Margin(8).Background(c)` and
  `.Background(c).Margin(8)` are the same: the margin is never filled, and the radius
  rounds that box's background and border and clips what's inside it.
- `.Padding` and `.Frame` still go by order and end a run. `.Padding(12).Background(c)`
  fills the padding, and `.Background(c).Padding(12)` doesn't, as today. In
  `.CornerRadius(8).Padding(12).Background(c)` the radius and background are in
  different boxes, so that background is square.
- Several margins in one run add up. For the others in one run, the last one written
  wins.

Merging happens when modifiers are written, not in layout, so `UiNode` sees one box
view and the tree stays shallow. The README states these rules in one short table.

Views:

- `Image(source)`: draws a loaded image at its natural size unless framed, with a
  `Fit` of `Stretch`, `Contain` or `Cover`. Tinted by an optional color.
- `ScrollView(axis)`: vertical by default. Offers its content unlimited length on
  that axis, clips to its own frame, and shows a scrollbar only when content overflows.
- `ListView`: a `ScrollView` of rows from a collection, with the selected item and a
  selection callback. Row content is the game's. It handles hover and selected
  backgrounds. It's named `ListView` because `List` would collide with
  `System.Collections.Generic.List<T>` in every game that imports `Super.Ui`.
- `Grid(columns, gap)`: fixed column count, equal cells, wrapping by row. Takes
  separate row and column gaps if given.
- `Button`: built on the public press-state view below. It has a disabled state that
  draws dimmed and ignores clicks.
- `Pressable`: a view whose content is a function of `Hovered` and `Pressed`, so games
  can build their own controls the way `Button` is built.

Stack `Spacing` is already the CSS gap and stays as it is. Stacks don't gain wrapping
in this work.

## Mouse

### Two speeds

Games run at 30 ticks a second by default and `Input` is readable only in `Update`.
That rate is too slow for anything following the cursor, so interface input has two
paths, both inside engine code:

- **Every drawn frame, in the host before `Ui.Update`:** hover, pressed looks, wheel
  scrolling, scrollbar dragging, tooltip timing, and the dragged preview. These read
  the newest pending mouse state through an internal route. Only the eyes care about
  them, which fits the rule in `docs/design.md` that what only the eyes care about is
  view.
- **On the tick, after `Input.Roll` and before `Game.Update`:** `OnClick`,
  selection, and drop callbacks. These are what may change game state, so a tick is
  the only place they run. A callback runs with `Input` readable and state changes
  open, exactly as if it were part of `Update`.

The second path means a click is recognized on a frame but delivered on the next
tick, in the order it happened. Nothing is lost if several happen between ticks.
`Ui` itself never reads `Input`, and that rule stays enforced.

In a headless run, frames and ticks alternate one to one, so a script produces the
same interface and the same callbacks each time. Hit testing uses the frames from the
last laid-out interface, in both runs.

### Rules

- Hit testing walks front to back, respects clipping, and respects rounded corners
  only as far as the view's rectangle. A clipped-away part of a view can't be hit.
- A click is a left press and release on the same element. Moving off it while held
  drops its pressed look; moving back restores it.
- The wheel goes to the innermost scroll area under the cursor that can still move
  that way. When it reaches the end, the wheel passes to the next one out.
- A scrollbar's thumb can be dragged, and it keeps following the cursor anywhere on
  the screen until release. Clicking the track jumps the thumb there.
- Content that shrinks clamps the scroll offset. Scroll offset is kept in the node, so
  it survives redraws and is lost when the view leaves the tree.
- Scrolling keeps hover correct: what's under a still cursor highlights.
- A drag starts after the cursor moves a few pixels while pressed, so a sloppy click
  stays a click. The preview follows the cursor at frame rate above everything, with
  `IgnoresPointer` behavior. A target whose `canDrop` accepts the payload shows as
  hovered. Releasing anywhere else cancels with no callback.
- Tooltips appear after a short hover, stay on screen by flipping away from window
  edges, ignore the pointer, and disappear on press, drag or scroll.
- Overlays (tooltip, drag preview) draw last, above the whole tree, and aren't clipped
  by scroll areas.

### Blocking the game

Anything drawing a background, border or image, or having a click, drag or scroll
behavior, blocks the mouse. Text alone doesn't. `.IgnoresPointer()` opts out, and
Reef's `StatsPanel` adopts it so its behavior doesn't change.

When the cursor is over something blocking, or a press or drag began on the interface,
mouse button reads and wheel reads through `Input` and `Actions` report nothing for
that tick, so games get this right with no code. `Input.MouseOverUi` says so too, for
games that want to know. `MousePosition` still reads truthfully. A press that began in
the game keeps belonging to the game until release, even if the cursor crosses a panel.

## Rendition

Rendition stays unaware of `Super.Ui`. It still sees an ordered list of quads, and
gains what a quad can do. Design table rows in `docs/design.md` stay true.

`OverlayQuad` gains:

- A corner radius, border width and border color, drawn in `overlay.frag` as a
  signed-distance rounded rectangle with one pixel of antialiasing.
- A clip rectangle and clip corner radius, tested per pixel in the fragment shader.
  This is decided over scissor rectangles: it's a single draw call, keeps draw order
  trivially correct, and is the only way to get rounded clipping.
- A texture index, where the glyph atlas is the default.

Images go in `ResourceTable`, the renderer's existing bindless table of 1024 textures,
and overlay quads index it, so images don't break batching or need a rebuilt atlas.
The overlay pipeline binds that set beside its atlas. Glyphs keep today's alpha-only
path with colors from sRGB, and images sample a real sRGB texture, so the two branches
must both end up correct against the sRGB swapchain.

Each quad has one clip. Nested clips intersect their rectangles and keep the innermost
radius that's set, so content can never pass a clip's rectangle, but it can show a few
pixels past a rounded corner where two rounded clips overlap. This is accepted to keep
the quad small.

`MaxQuads` stays a limit, and `UiTree` culls anything entirely outside its clip before
adding a quad, so a long list costs what's on screen. `UiTree` knows the limit and
reports reaching it: debug builds throw, and other builds write one line to standard
error the first time, then draw what fits. A shipped game keeps running, and nothing
is dropped silently. The per-frame allocation rule still applies to Rendition: nothing
here may allocate per frame in the render path.

`--check` exists only in `Rendition.Sample`, which draws no overlay today. The sample
gains overlay quads using images, rounded corners, borders and clips, so the allocation
and validation checks cover the new path. Super doesn't gain a `--check` flag.

## Images as content

`Image` takes a handle from `Content`, loaded like other assets: decoded on a
background thread, uploaded one per frame, and waited for when first drawn. Super adds
`StbImageSharp`, which `tools/Rendition.Import` already uses. Headless runs decode the
file for its size and upload nothing, so layout is identical with no GPU.

## Assets

Icons come from a CC0 icon set, added to `CREDITS.md` with its source and license, as
existing assets are. Nothing goes in the repo without a license that allows it.

## Testing

- Layout, hit testing, scrolling, drag and drop, click rules and input blocking are
  all CPU code and get tests in `tests/Super.Tests/Ui` that run with no window.
- Tests assert on `ShownText`, frames, and `UiTree` quads including their clip rects.
- Rendition changes are checked by `--check`, the Vulkan validation layers, and
  screenshots through `--screenshot`, never by capturing the desktop.
- Every rule under Mouse has a test. Each anti-goal has one where it can be automated.
  Lag behind the cursor can't be, so it's checked by hand in the running game.

### Scripted input in the window

Today `ScriptedInput` runs only headless, and `--screenshot` and `--record` have no
input, so they can only show an idle screen. The user decided the engine gains a way
to play a script through the real window, so tooltips, drags, hover looks and scrolled
lists can be captured and repeated.

A script is the same timed input `ScriptedInput` already plays, keyed by tick, and
real mouse input is ignored while one plays. It's fed in where the window normally
feeds `Input`, so it goes through the same path as a player. A game supplies it from
code, so `examples/Crafting` can offer named scenes, and a `--script <name>` flag picks
one. Screenshots of each state in the example are the visual proof for this work.

## Docs

`docs/design.md` gets rows for the two-speed mouse path and for per-pixel clipping,
and the README's interface section covers the new views.

## Left to the builder

- Exact parameter lists, default colors, scrollbar size, tooltip delay, drag threshold.
- Scrollbar styling, as long as it's visible and easy to grab.
- How pending mouse state reaches the frame-rate path.
- Which CC0 icon set, and the game data in the example.
- Breaking the work into commits.

## Calls made by the designer

The user chose layout C, full mouse support, drag and drop, the one-box modifier rule
and scripted input in the window, and asked for images, scroll areas, lists and
everyday CSS styling. These other calls were made on their behalf:

- A new `examples/Crafting`, instead of adding to Reef or Rooms.
- `Grid`, `Button`, `Pressable` and tooltips join the requested views, because the
  screen needs them.
- Gradients and shadows are left out.
- The two-speed mouse path, per-pixel clipping, and images in the bindless table.
- Everything drawn blocks the mouse by default, and Reef opts out.
- Input is hidden from games automatically when the interface takes it.
- `ListView` isn't virtualized.
- CC0 icons.
- One clip per quad, approximate where two rounded clips overlap.
- Reaching `MaxQuads` throws in debug builds and warns once in others.
- `--check` coverage comes from `Rendition.Sample`, not a new engine flag.
