# Interfaces

Worlds are flat lists, redrawn every frame. Interfaces are the opposite: they're trees,
and most of a tree stays the same from one frame to the next. So `Super.Ui` works like
SwiftUI. You describe the interface with small records, and Super works out what changed.

```csharp
using Super;
using Super.Ui;

record StatRow(string Label, string Value) : UiView
{
    public override UiView Body => new HStack(Spacing: 24f)
    {
        new Text(Label).Foreground(Color.Hex("#9FD3E0")),
        new Spacer(),
        new Text(Value).MonospacedDigit(),
    };
}

record ColonyPanel(int Workers, int Wood) : UiView
{
    public override UiView Body => new VStack(HorizontalAlignment.Leading, Spacing: 4f)
    {
        new Text("Colony").Bold().FontSize(16f),
        new StatRow("Workers", $"{Workers}"),
        new StatRow("Wood", $"{Wood}"),
    }
    .Padding(12f)
    .Background(Color.Hex("#06202C", 0.72f))
    .FixedSize();
}

class MyColony(Colony colony) : IGame
{
    public UiView? Ui => new ColonyPanel(colony.Workers.Count, colony.Wood)
        .Padding(16f)
        .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    // Update and Draw as usual.
}
```

A component is a record with a `Body`. `ColonyPanel` and `StatRow` are components, and
you use your own exactly like the built-in ones. Super reads `Ui` every frame, so always
describe the whole interface and let Super find the differences.

Interface code runs on the CPU only. Layout and text are real in headless runs, so
[a test can check](testing.md#checking-the-screen) what text is shown and where.

## Views

| View | What it does |
| --- | --- |
| `Text("Hello")` | Text. It wraps at spaces when it's offered too little width. See [Text that doesn't fit](#text-that-doesnt-fit) |
| `VStack`, `HStack` | Children top to bottom, or left to right |
| `ZStack` | Children on top of each other, the last one in front |
| `Spacer()` | Pushes its neighbors apart along the stack |
| `Image(content.Image("icon.png"), ImageFit.Contain)` | An image, stretched, fitted (`Contain`) or filling (`Cover`), with an optional tint |
| `ScrollView()` | Scrolls what's inside it with the wheel or a draggable bar. Give it a height, or it grows to fit |
| `ListView<T>(items, row, selected, onSelect)` | A scrolling list of rows you can hover and pick |
| `Grid(columns, columnGap, rowGap)` | Children in equal columns, wrapping onto new rows |
| `Pressable(look => view, onClick)` | Anything clickable, drawn from whether it's hovered or pressed |
| `Button("Craft", onClick, Disabled: true)` | A ready-made `Pressable` with a label |

## Modifiers

Modifiers wrap a view, so their order usually matters.

| Modifier | What it does |
| --- | --- |
| `.Padding(all)`, `.Padding(horizontal, vertical)`, `.Padding(top, trailing, bottom, leading)` | Adds room around the view |
| `.Margin(...)` | The same, but outside the view's background |
| `.Frame(width, height, maxWidth, maxHeight, alignment, minWidth, minHeight)` | Fixes the size, or lets the view stretch |
| `.FixedSize()` | Uses the size the view would like, whatever it's offered |
| `.Background(color)` | Fills the view's area |
| `.Border(width, color)` | Draws a line inside the view's edge, following its corners |
| `.CornerRadius(radius)` | Rounds the background, border and image, and clips what's inside |
| `.Opacity(value)` | Fades the view and everything inside it |
| `.Foreground(color)`, `.FontSize(pixels)`, `.Bold()` | Styles text here and everywhere inside |
| `.MonospacedDigit()` | Gives digits one width, so changing numbers don't jiggle |
| `.LineLimit(lines)` | Keeps text here and everywhere inside to that many lines, ending cut text in "…" |
| `.Id(key)` | Names a view among its siblings, and lets a test or script [find it on screen](testing.md#finding-a-view) |
| `.OnClick(action)` | Runs on a left click |
| `.Tooltip(view)` | Shows a view near the cursor after a short hover |
| `.Draggable(payload, preview)`, `.DropTarget<T>(onDrop, canDrop)` | Lets the mouse carry a value from one view to another |
| `.IgnoresPointer()` | Lets the mouse through, like CSS's `pointer-events: none` |

### The box

Five modifiers written next to each other make one box, in any order, as in CSS:

| Written | Result |
| --- | --- |
| `.Background(c).Margin(8f)` or `.Margin(8f).Background(c)` | Same thing: the margin is never filled |
| `.Border(1f, c).CornerRadius(6f)` or the reverse | Same thing: the border follows the corners |
| `.Margin(4f).Margin(4f)` | Margins in one box add up, to 8 |
| `.Padding(12f).Background(c)` | Padding comes first, so the background fills it |
| `.Background(c).Padding(12f)` | Padding comes after, so it stays outside the background |
| `.Background(c).Padding(1f).CornerRadius(8f)` | Padding ends the box, so that background stays square |

`Background`, `Border`, `CornerRadius`, `Opacity` and `Margin` are the five. `Padding` and
`Frame` go by order and end the box. Gaps are the `Spacing` on a stack and the gaps on a
`Grid`.

## Colors

`Color` takes whichever notation you like, and each one takes an optional alpha:

```csharp
Color.Hex("#06202C", 0.72f);        // also #RGB, #RGBA and #RRGGBBAA
Color.Rgb(6, 32, 44);               // 0 to 255
Color.Hsl(198f, 0.76f, 0.1f);       // hue in degrees, the rest 0 to 1
Color.Oklch(0.72f, 0.12f, 210f);    // lightness 0 to 1, chroma 0 to about 0.4, hue in degrees
Color.White with { A = 0.5f };      // change just the opacity
```

Oklch is the best one for building a palette, because colors with the same lightness
look equally bright. A color too vivid for a screen gets clipped.

`Color` is for interfaces. 3D colors and lights stay as `Vector3`, because they're
linear and a bright light can go well past 1.

## Layout

Layout works like SwiftUI's, and does what flexbox does:

1. A parent offers its child some space.
2. The child picks its own size. The parent has to accept the answer.
3. The parent places the child, using its alignment.

Stacks share their space out starting with the least flexible child, and spacers go
last. To make something grow like `flex-grow`, give it
`.Frame(maxWidth: UiExtensions.Infinity)`. To pin something to a corner, wrap it in a
frame that fills the screen, as `MyColony` does above. The top of the tree is centered
otherwise.

### Text that doesn't fit

A stack offers each piece of text the width it would like when there's room. When
there isn't, text wraps at its spaces and gets taller, and a short label such as
"Thatch 8" can end up split from its number, spilling out of a card with a fixed height.
Wrapping is the default because paragraphs need it. For labels, choose what happens
instead:

| Write | What happens when it doesn't fit |
| --- | --- |
| nothing | Wraps onto more lines, and grows taller than it was offered |
| `.LineLimit(1)` | Stays on one line inside its frame, cut with "…" at the end |
| `.LineLimit(n)` | Wraps onto at most `n` lines, the last cut with "…" |
| `.FixedSize()` | Stays whole on one line and spills past its frame, overlapping its neighbours |

`.LineLimit` reaches every text inside it, so one on a row covers all its labels:

```csharp
new HStack { new Text("Wood 16"), new Text("Stone 6"), new Text("Thatch 8") }.LineLimit(1)
```

`run.ShownText` reports cut text as it shows, such as `"Thatc…"`, so a test sees the
cut too. Only cut text is reported differently; text that wraps is reported whole.

## State

Most interfaces only show game state. For anything the interface owns itself, such as
which tab is selected, use `UseState`. Super keeps the value for as long as that view
stays in the tree:

```csharp
record Tabs : UiView
{
    public override UiView Body
    {
        get
        {
            var selected = UseState(0);
            return new HStack(Spacing: 8f)
            {
                new Button("Items", () => selected.Value = 0),
                new Button("Recipes", () => selected.Value = 1),
                new Text($"Tab {selected.Value}"),
            };
        }
    }
}
```

Reading `Value` during `Body` signs that component up for changes, and setting it runs
only the components that read it. Call `UseState` the same number of times and in the
same order on each run. Hand a `State<T>` to a child to let the child use it too.

The rule is the same as for views: if gameplay cares, it's game state, and it belongs in
your own classes, not in `UseState`.

## The mouse

Clicks, drops and list picks run just before `Update`, on the tick they happened, so
they can change game state and replay with a seed. Hover, press and scrolling follow the
mouse every frame, however slowly the game ticks.

```csharp
new Button("Craft", () => bag.Craft(recipe), Disabled: !bag.Has(recipe));

new Pressable(look => Slot(stack).Background(look.Hovered ? hover : fill))
    .Draggable(slotNumber, new Image(icon).Frame(56f, 56f))
    .DropTarget<int>(from => bag.Move(from, slotNumber));
```

In a tick, the order is: clicks, then `Update`, then the next `Draw`. So `Update` already
sees what a click changed on the tick it happened, and the press the button took never
reaches `Input`, so `Pressed(MouseButton.Left)` stays false. When a button and a key do
the same thing, have both call one method that does all of it, such as putting the other
tool away, rather than comparing state against last tick in `Update` to find out what a
click did:

```csharp
new Button("Harvest", () => tools.TakeOut(harvest));      // the button

if (actions.Pressed(Control.Harvest))                     // the key, in Update
{
    tools.TakeOut(harvest);
}
```

A press that moves off a button and lets go there doesn't click. While the mouse is over
the interface, `Input.MouseOverUi` is true, so a game can leave those clicks alone. To
have a panel let clicks through to the game, as a stats readout should, use
`.IgnoresPointer()`.

A `ListView` compares its list by reference, so hand it a new one when the items change.

[The crafting bench](../examples/Crafting/CraftingGame.cs) uses all of this.

## Scale

Sizes in an interface are pixels, until you scale it. At a scale of 2, one unit covers two
pixels, so text, padding, borders and corners are all twice as big. That's what an
interface made for a desktop window needs to be read in a video watched on a phone:

```
dotnet run -c Release --project MyColony -- --size 1080x1920 --ui-scale 2.5 --record clip.mp4 --seconds 15
```

`--ui-scale` overrides `GameOptions.UiScale`, which is 1 unless the game sets it. The
interface lays itself out in the screen's size divided by the scale, so a 1080 pixel wide
screen at 2.5 is 432 units wide. Text is drawn at its size in pixels, not stretched, so it
stays sharp. `FrameOf`, `ShownText` and everything else the interface reports are in
units. The mouse is turned into units for you, and an `InputScript` or a test that clicks
a view by its id aims at where it's drawn. `InputScript.MoveMouse(position)` and
`Input.MousePosition` stay in pixels. A headless test sets the scale with `run.UiScale`.

A scale that isn't a number above zero is refused. The characters all share one 1024 by
1024 image, so very large text at a large scale can run out of room, and characters that
don't fit aren't drawn.

## How it stays cheap

This is a virtual tree, like React's and SwiftUI's. The records are cheap descriptions
that live for moments. Behind them sits a tree of long-lived nodes, which hold state and
layout. Each frame, the reconciler walks the new description against those nodes:

- A record that equals the last one is skipped, with everything inside it.
- If nothing changed, there's no layout and no drawing work. The panel in Reef only
  redoes its work twice a second, when its numbers change.
- A view keeps its state while it stays in the same place with the same type. A
  different type in that place starts fresh.
- `show ? view : null` keeps its slot when it's null, so what follows doesn't move.
  In a list that changes, use `.Id(item.Id)`, like a `key` in React, so rows keep their
  state as they move.
- A lambda that captures a variable is a new object each run, so a record holding one
  never equals the last. That view and its parents are laid out again every frame. For a
  screen with a few hundred views that's fine. For something always on screen in a tight
  budget, keep the delegate in a field, so it stays equal.

The default font is [Inter](https://rsms.me/inter/), built into `Super.dll`, so text
works with no files around.

## Showing how the game runs

Ask for `GameStats` in your constructor and read `Latest`, which updates twice a second.
It holds the frame rate, frame time, bytes allocated per frame on the game's thread, heap
size and garbage collections. Work done for the interface isn't counted, so a stats panel
doesn't report itself. Reef's panel is in
[StatsPanel.cs](../examples/Reef/StatsPanel.cs).

These numbers come from the real clock, so only show them. If they reach `Update`, a seed
stops repeating.

## Not yet

There are no text inputs, and the interface can't be driven by keyboard or gamepad yet.
