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
| `Image(content.UiImages.Image("icon.png"), ImageFit.Contain)` | An image, stretched, fitted (`Contain`) or filling (`Cover`), with an optional tint |
| `ScrollView()` | Scrolls what's inside it with the wheel or a draggable bar. Give it a height, or it grows to fit |
| `ListView<T>(items, row, selected, onSelect)` | A scrolling list of rows you can hover and pick |
| `Grid { new GridRow { ... } }`, `Grid(Columns: 6)` | A table whose columns line up, or equal columns filled left to right. See [Grids](#grids) |
| `FlowStack(spacing, lineSpacing)` | Children in a row that wraps onto new lines, like words |
| `ViewThatFits { ... }` | Shows the first child that fits the room |
| `LazyVStack<T>(items, row, rowHeight)` | A column that only makes the rows near the screen, for very long lists. See [Lazy lists](#lazy-lists) |
| `Pressable(look => view, onClick)` | Anything clickable, drawn from whether it's hovered or pressed |
| `Button("Craft", onClick, Disabled: true)` | A ready-made `Pressable` with a label. See [Controls](#controls) |
| `Toggle("Music", on, set)` | A switch, or a checkbox with `Style = ToggleStyle.Checkbox` |
| `Slider(value, set, min, max)` | Picks a number by dragging, smoothly or in `Step`s |
| `Stepper("Players", count, set, min, max)` | A whole number with minus and plus buttons |
| `Picker<T>("Difficulty", picked, options, set)` | One of a few `PickerOption`s, segmented or as radio buttons |
| `ProgressView("Loading", done, total)` | How far along something is, as a bar or a ring |
| `Gauge("Health", value, min, max)` | A reading on a scale, as a ring open at the bottom or a bar |
| `Rectangle`, `RoundedRectangle(radius)`, `Capsule`, `Circle`, `Ellipse` | A shape that fills the space it's offered. See [Shapes](#shapes) |
| `Arc(start, sweep, thickness)` | Part of a ring, for progress rings, cooldowns and gauges |
| `new Icon(IconName.Search)` | A line icon, sized and coloured like the text around it |
| `Divider()` | A thin line across a stack, either way |
| `Badge("Rare", BadgeStyle.Secondary)` | A short tag for a count, a status or a rarity |
| `Label("Settings", new Icon(IconName.Menu))` | Text with an icon before it |
| `KeyHint("E")` | A keycap, for prompts such as "Press E to open" |
| `Card { ... }` | A raised panel for views that belong together. See [Containers](#containers) |
| `LabeledContent("Gold", "1,250")` | A label with a value or a control at the end of the row |
| `Form { new Section("Audio") { ... } }` | A settings screen of sections, with a line between each row |
| `DisclosureGroup("Advanced") { ... }` | A heading with a chevron that shows or hides what's under it |
| `TabView { new Tab("Video", page) }` | Pages a bar of tabs switches between |
| `NavigationStack<T>(path, title, root, destination)` | Pages that go deeper, with a bar and a way back |

## Modifiers

Modifiers wrap a view, so their order usually matters.

| Modifier | What it does |
| --- | --- |
| `.Padding(all)`, `.Padding(horizontal, vertical)`, `.Padding(top, trailing, bottom, leading)` | Adds room around the view |
| `.Margin(...)` | The same, but outside the view's background |
| `.Frame(width, height, maxWidth, maxHeight, alignment, minWidth, minHeight)` | Fixes the size, or lets the view stretch |
| `.FixedSize()` | Uses the size the view would like, whatever it's offered |
| `.Background(color)`, `.Background(Paint.Linear(...))` | Fills the view's area with a colour or a [gradient](#gradients) |
| `.Background(view, alignment)`, `.Overlay(view, alignment)` | Puts a view behind or in front, at the size of this one. Neither changes the size |
| `.Offset(x, y)` | Moves where the view is drawn and clicked, and leaves its neighbours where they were |
| `.Hidden()` | Hides the view but keeps its room. It can't be clicked or focused, and tests don't see it |
| `.LayoutPriority(priority)` | Lets a stack serve this view first when it's short of room |
| `.AspectRatio(ratio, ContentMode.Fit)` | Keeps the view at one width to height, the biggest that fits or, with `Fill`, the smallest that covers |
| `.Border(width, color)` | Draws a line inside the view's edge, following its corners |
| `.CornerRadius(radius)`, `.CornerRadius(new CornerRadii(...))` | Rounds the background, border and image, and clips what's inside. `CornerRadii` rounds each corner on its own |
| `.Shadow(ShadowSize.Medium)`, `.Shadow(color, blur, x, y, spread)` | Casts a soft [shadow](#shadows) under the box |
| `.Opacity(value)` | Fades the view and everything inside it |
| `.Foreground(color)`, `.Foreground(Tone.Muted)`, `.FontSize(pixels)`, `.Font(font)` | Styles text here and everywhere inside. A `Tone` takes its colour from the theme |
| `.TextStyle(TextStyle.Headline)` | Sets the size and weight from the theme's [text styles](#text-styles) |
| `.Bold()`, `.Weight(FontWeight.Medium)`, `.Italic()` | Sets how heavy text is, or slants it |
| `.TextAlign(TextAlignment.Center)` | Lines up each line of text inside its frame |
| `.Theme(theme)` | Gives everything inside [a theme](#themes) |
| `.Disabled()` | Turns off every control inside: they fade and ignore clicks. `.Disabled(false)` further in can't turn them back on |
| `.ControlSize(ControlSize.Small)` | Makes the controls inside smaller or larger |
| `.MonospacedDigit()` | Gives digits one width, so changing numbers don't jiggle |
| `.LineLimit(lines)` | Keeps text here and everywhere inside to that many lines, ending cut text in "…" |
| `.Id(key)` | Names a view among its siblings, and lets a test or script [find it on screen](testing.md#finding-a-view) |
| `.OnClick(action)` | Runs on a left click |
| `.Tooltip(view)` | Shows a view near the cursor after a short hover |
| `.Draggable(payload, preview)`, `.DropTarget<T>(onDrop, canDrop)` | Lets the mouse carry a value from one view to another |
| `.Draggable(start => payload, payload => preview)` | Chooses what to carry at the press, from the button and the Shift, Control and Alt keys held (`DragStart`), or null not to drag. On a Mac, Cmd counts as Control, since Ctrl with a click means a right-click there |
| `.IgnoresPointer()` | Lets the mouse through, like CSS's `pointer-events: none` |
| `.OnSlide(fraction => ..., inset)` | Follows the left button held on the view, with how far across it the cursor is, for [controls of your own](#controls-of-your-own) |
| `.Semantics(role, label, value, isChecked)` | Says what a view of your own means, so tests [read it like a built-in control](testing.md#reading-controls) |

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

## Themes

The built-in elements take their look from a `Theme`: its colours, corners, shadows, text
styles and control sizes. `Theme.Dark` is the default and `Theme.Light` is the other. Both
are neutral greys, so they fit most games. Change what you need with `with` and put it on
any view, and everything inside takes it:

```csharp
var theme = Theme.Dark with
{
    Colors = Theme.Dark.Colors with { Primary = Color.Hex("#3B82F6") },
    Radius = 2f,
};

new VStack(new Text("Paused"), new Button("Resume", Resume)).Theme(theme);
```

The colours are in `theme.Colors`, and the shadows in `theme.Shadows`:

| Colour | Used for |
| --- | --- |
| `Background`, `Foreground` | Behind everything, and the text on it |
| `Surface`, `Raised` | Panels and cards, and what floats above them, such as menus |
| `Muted`, `MutedForeground` | Quiet fills such as a slider's track, and text that matters less |
| `Hover` | A row or quiet button under the pointer |
| `Primary`, `Secondary`, `Destructive`, each with a `...Foreground` | Buttons and badges, and the text on them. `Primary` also fills a switch that's on, the filled part of a slider, and progress |
| `Success`, `Warning` | Statuses |
| `Border`, `Input`, `Ring` | Lines between things, the edge of a field, and the focus ring |
| `Scrim` | Laid over the game behind a dialog |

Your own views can use the same colours through a `Tone`, so they change with the theme:
`.Foreground(Tone.Muted)` is the theme's `Colors.MutedForeground`. Corners are `RadiusSmall` (4),
`Radius` (6) and `RadiusLarge` (10).

`.Disabled()` and `.ControlSize(...)` also reach everything inside them. A control turned off
fades and ignores the mouse, and `ControlSize.Small`, `Regular` and `Large` set how tall
controls are, their padding and their text size, from the theme's `Small`, `Regular` and
`Large` metrics. The theme also holds the sounds the controls make, in `Sounds`; see
[Sounds](#sounds).

### Text styles

`.TextStyle(...)` sets text's size and weight by what it is, as SwiftUI does, from the
theme's `Typography`:

| Style | Size | Weight |
| --- | --- | --- |
| `LargeTitle` | 30 | Bold |
| `Title` | 24 | SemiBold |
| `Title2` | 20 | SemiBold |
| `Title3` | 17 | SemiBold |
| `Headline` | 14 | SemiBold |
| `Body` | 14 | Regular |
| `Callout` | 13 | Regular |
| `Subheadline` | 13 | Medium |
| `Footnote` | 12 | Regular |
| `Caption` | 11 | Medium |

Change one with `Theme.Dark with { Typography = Typography.Default with { Title = new(28f, FontWeight.Bold) } }`.

### Gradients

`.Background(...)` and a shape's `.Fill(...)` take a `Paint`, which is a colour or a
gradient. A colour turns into a `Paint` by itself.

```csharp
Paint.Linear(top, bottom);                                       // top to bottom
Paint.Linear(from, to, angle: 90f);                              // left to right, as CSS's angles go
Paint.Linear(from, to, Alignment.TopLeading, Alignment.BottomTrailing);
Paint.Radial(center, edge, at: Alignment.Top, radius: 200f);     // the radius reaches the farthest corner unless given
```

Gradients mix in sRGB, as CSS does, and fade into a clear colour without turning grey.

### Shadows

`.Shadow(ShadowSize.Small)`, `Medium` and `Large` cast the theme's shadows. For one of your
own, give its colour, blur and offset: `.Shadow(Color.Black with { A = 0.4f }, blur: 16f, y: 6f)`.
A shadow follows the box's corners and isn't seen through a see-through background.

### Shapes

Shapes fill the space they're offered, so give one a size with `.Frame(...)` or let a stack
stretch it. A shape takes the text colour around it unless it's given `.Fill(paint)`, and
`.Stroke(color, width)` draws its outline inside the edge:

```csharp
new Circle().Fill(Color.Hex("#22C55E")).Frame(width: 10f, height: 10f);
new RoundedRectangle(8f).Stroke(Color.White, 2f).Frame(width: 120f, height: 40f);
new Arc(Start: 0f, Sweep: cooldown, Thickness: 3f).Frame(width: 32f, height: 32f);
```

An `Arc` runs clockwise from the top. Its start and sweep are fractions of a turn, so
`new Arc(0f, 0.25f)` is the top right quarter.

## Controls

The controls show the value they're given and tell you when the player changes it, as
React's controlled inputs do. Keep the value in your game, pass it in, and store what
`OnChange` hands back. The change runs on the next tick, just before `Update`, like a click:

```csharp
new VStack(HorizontalAlignment.Leading, 12f)
{
    new Toggle("Subtitles", settings.Subtitles, on => settings.Subtitles = on),
    new Slider(settings.Volume, volume => settings.Volume = volume) { Label = "Volume" },
    new Slider(settings.Fov, fov => settings.Fov = fov, 60f, 120f) { Step = 5f, Label = "Field of view" },
    new Stepper("Players", lobby.Players, players => lobby.Players = players, 1, 4),
    new Picker<Difficulty>("Difficulty", game.Difficulty, Difficulties, picked => game.Difficulty = picked),
}
```

A control without an `OnChange` only shows its value. Every control fades and ignores the
pointer when it's `Disabled` or inside `.Disabled()`, and follows the `.ControlSize(...)`
around it.

| Control | Choices |
| --- | --- |
| `Button` | `Style`: `Default` (filled with the primary colour, for the main action), `Secondary`, `Outline`, `Ghost`, `Destructive` and `Link`. `Icon` puts an icon before the label, and with an empty label it's a square icon button. `FullWidth` stretches it across its row with the label in the middle |
| `Toggle` | `Style`: `Switch`, which fills its row with the label at the start, or `Checkbox`, a box before the label. Clicking the label toggles it too |
| `Slider` | `Min`, `Max` and `Step`. A press on the track jumps there, and dragging follows the cursor even off the track. `Label` names it for tests |
| `Stepper` | `Min`, `Max` and `Step`. Each button turns off at its end of the range |
| `Picker<T>` | `Style`: `Segmented`, side by side with the picked one raised, `Radio`, one under the other, or `Menu`, a button showing the picked one that opens a menu with it checked |
| `ProgressView` | `Style`: `Linear` or `Circular`. A bar with a label shows the percentage beside it, and a ring shows it inside. Made with only a label, `new ProgressView("Connecting")`, it doesn't know how far along it is, so a ring spins and a piece of the bar sweeps across |
| `Gauge` | `Style`: `Circular` or `Linear`, `Tint` for its own colour, and `ValueLabel` for the reading shown in place of the number |

![Button styles, toggles, sliders and pickers in the storybook](media/ui-controls.webp)

A `Button` without a `Style` is the main action, filled with the theme's primary colour.
It used to be a quiet grey, so give the buttons beside the main one `ButtonStyle.Secondary`
or `Outline`:

![The Crafting example's Craft button, grey before and filled with the primary colour after](media/ui-button-default.webp)

### Controls of your own

`.OnSlide(...)` and `.Semantics(...)` are what the built-in controls are made with. A
volume knob or a colour wheel can use them too:

```csharp
new Knob(level)
    .OnSlide(across => SetLevel(across), inset: 8f)
    .Semantics(UiRole.Slider, "Level", UiSemantics.Number(level));
```

`OnSlide` hands over how far across the view the cursor is, from 0 to 1, once a tick for
as long as the left button stays down. `inset` keeps that much of each end out of the
range, for a thumb whose middle stops short of the edge.

## Focus, the keyboard and the gamepad

Every built-in control can take focus, so a menu works without a mouse. The focused control
has a ring round it:

| Input | What it does |
| --- | --- |
| Tab, Shift+Tab | The next or the previous control, in the order they're laid out, round to the start again |
| The arrows, the d-pad, the left stick | The nearest control that way. On a slider, stepper or picker, left and right change its value instead |
| Enter, Space, the gamepad's South button | Presses the focused control |
| Escape, the gamepad's East button | Leaves a text field |

Held, the d-pad and the stick move once, then after a short wait again and again. A control
that's turned off, hidden or gone is passed by, and when the focused one goes, focus lets go.
A click anywhere but a text field lets go too, and the ring stays hidden while the mouse is
in use: the first arrow after a click only shows it again.

![Focus on a level button, passing the locked one by, and typing into a secure field](media/ui-focus-and-text-fields.webp)

Keys and buttons the interface uses are taken from the game. While a control has focus,
`input.Pressed(Key.Enter)` stays false for the press that pressed it, and the game sees no part of
it, not even the release. The same goes for the gamepad's buttons, and the left stick reads as
centred while it steers. With nothing focused, every key and button goes to the game, so a game
with no interface on screen sees all its input as before.

`.DefaultFocus()` gives focus to the first control inside it as soon as it appears, with the
ring hidden. A main menu wants that, so the gamepad's South button starts the game from the
start. With nothing focused, the arrows come back to it:

```csharp
new VStack(Spacing: 12f)
{
    new Button("Play", game.Start),
    new Button("Options", () => screen = Screen.Options) { Style = ButtonStyle.Secondary },
    new Button("Quit", game.Quit) { Style = ButtonStyle.Ghost },
}.DefaultFocus()
```

Without one, Tab still starts on the first control, and the arrows stay with the game until
something has focus.

`.Focusable(...)` makes a view of your own a stop. It takes what Enter does and, for a
control with a value, what left and right do, as -1 or 1. Leave the first out to press the
click inside the view. A focusable view holds whatever can take focus inside it, so a
stepper's two buttons are one stop:

```csharp
new Card(chest)
    .Focusable(() => chest.Open(), step => chest.Turn(step))
    .Semantics(UiRole.Button, "Old chest");
```

A focused control inside a `ScrollView` is scrolled into view.

## Text fields

`TextField` is one line of text to type. It's a control like the others: give it the text
and store what `OnChange` hands back. The prompt shows in a muted colour while it's empty.
`SecureField` shows a dot for each character and won't copy or cut:

```csharp
new TextField("Player name", profile.Name, name => profile.Name = name),
new SecureField("Password", login.Password, password => login.Password = password)
{
    OnSubmit = _ => login.SignIn(),
},
```

A click or Tab focuses a field, and a click places the caret. A focused field takes every key
except the modifiers and F1 to F25, so typing a W doesn't walk the player. Tab, Up, Down, Enter
and Escape leave it, and Enter calls `OnSubmit` first. Inside the field:

| Keys | What they do |
| --- | --- |
| Left, Right, Home, End | Move the caret. With Ctrl (Command on a Mac) Left and Right go a word at a time |
| Shift with any of those | Selects |
| Backspace, Delete | Delete a character, the selection, or with Ctrl (Command) a word |
| Ctrl+A, C, X, V (Command on a Mac) | Select all, copy, cut and paste. A paste becomes one line |

`OnChange` runs once a tick at most, with the text as it ends up, however many keys came in.
Characters below a space are dropped. A field that's `Disabled`, or inside `.Disabled()`,
fades and can't be focused.

## Popups

Menus, popovers, sheets, alerts and confirmation dialogs open over everything else. Each is
declared on the view it belongs to, so it reads the state round it and takes its theme. Keep
whether it's open in your game, as with a control's value, and set it back when it closes:

```csharp
new Button("Delete save", () => asking = true) { Style = ButtonStyle.Destructive }
    .Alert(asking, () => asking = false, new Dialog("Delete this save?", "You can't undo this.")
    {
        Buttons =
        [
            new DialogButton("Cancel", null, DialogRole.Cancel),
            new DialogButton("Delete", () => saves.Delete(slot), DialogRole.Destructive),
        ],
    })
```

| Popup | What it is | Closes with |
| --- | --- | --- |
| `Menu` | A button that opens a list of `MenuItem`s under it | Picking a row, a click outside, Escape or East |
| `.ContextMenu(items)` | The same list, where the player right-clicks the view or holds a finger on it | The same |
| `.Popover(open, onDismiss, content)` | A small card under the view, or above it when there's no room below | A click outside, Escape or East |
| `.Sheet(open, onDismiss, content)` | A card in the middle of the screen over a scrim, for a form | A click on the scrim, Escape or East |
| `.Alert(open, onDismiss, dialog)` | A question in the middle of the screen, with its buttons in a row | Only its buttons. Escape and East press Cancel, or the only button |
| `.ConfirmationDialog(open, onDismiss, dialog)` | A choice of actions at the bottom of the screen, one under the other, with Cancel apart | Its buttons, and a click outside, Escape and East press Cancel |

A `MenuItem` has an `Icon`, a `Shortcut` shown at its trailing edge (the game still handles the
key), and can be `Destructive`, `Disabled` or `Checked`. `MenuItem.Divider` draws a line between
groups. A `DialogButton` is `Default`, `Cancel` or `Destructive`, and an alert with no buttons has
an OK. Picking a row or a button runs its action and then `onDismiss`, on the tick like a click.

While a popup is open, it takes the pointer: a click outside it closes it, or does nothing for an
alert, and never reaches what's under it. Tab, the arrows and the d-pad stay inside it. It opens
with focus on its `.DefaultFocus()`, the checked row of a menu, or a dialog's last ordinary button
(Cancel when the only other one is destructive), and when it closes, focus goes back to the
control that opened it.

Toasts are short notes in the bottom corner that leave on their own. Make one `Toasts`, put it on
the root of the interface, and show a toast from anywhere in the game:

```csharp
var toasts = new Toasts();

UiView Ui => new GameScreen(world).Toasts(toasts);

void Saved(int slot) => toasts.Show(new Toast("Game saved", $"Slot {slot}") { Icon = IconName.Check });
```

Each shows for four seconds, or the seconds you pass, and a click or tap takes it away sooner.
`MaxShown`, three by default, is how many show at once, and another one lets the oldest go.
Toasts don't take the pointer or focus, so the game under them keeps working. Their time only runs
while the interface updates.

Tests find popups in `run.Elements` like any control: a menu's rows have `UiRole.MenuItem`, a
dialog `UiRole.Dialog` with its message as its `Value`, and a toast `UiRole.Toast`. A `Menu` is
`Checked` while it's open.

![The storybook's menu, open under its button](media/ui-popups.webp)

## The storybook

The [Storybook example](../examples/Storybook) shows every element on its own and in its
variations, in both themes. Run it to see what's there, or with `--story badges --theme light`
to open one page. The `buttons`, `toggles`, `sliders`, `steppers`, `pickers`, `progress`
and `gauges` stories keep their values, so you can try each control, and `text-fields` and
`focus` show typing and moving with the keys. The `focus` and `typing` input scripts play
those for a shot, with `--story focus --script focus`. The `touch` story and script show taps,
long presses and flings. The `menus`, `dialogs`, `sheets` and `toasts` stories have every popup,
and the `menu`, `context`, `alert`, `confirm`, `sheet`, `popover` and `toasts` scripts open them.
The `layers`, `grids`, `flows` and `lazy-lists` stories show the [layout](#layout) views, and
`cards`, `forms`, `tabs` and `navigation` show the [containers](#containers). The `animation`
story shows what moves, and has a switch that turns it off. The showcases, `settings`,
`inventory`, `main-menu`, `hud` and `character-creator`, are whole screens a game would have,
built only from these elements, and each one works.

![The storybook's shapes page in the dark theme](media/storybook.webp)

![The main menu, heads-up display, inventory and character creator showcases](media/ui-showcases.webp)

## Layout

Layout works like SwiftUI's, and does what flexbox does:

1. A parent offers its child some space.
2. The child picks its own size. The parent has to accept the answer.
3. The parent places the child, using its alignment.

Stacks share their space out starting with the least flexible child, and spacers go
last. When there's room for every child at its ideal size, each gets at least that, so
nothing wraps in a row that fits it. To make something grow like `flex-grow`, give it
`.Frame(maxWidth: UiExtensions.Infinity)`. To pin something to a corner, wrap it in a
frame that fills the screen, as `MyColony` does above. The top of the tree is centered
otherwise.

![Overlays, offsets, hiding, grids, flow stacks, views that fit and a lazy list in the storybook](media/ui-layout.webp)

When a row is short of room, `.LayoutPriority(1f)` on one child makes the stack serve it
first. The stack keeps back only what the others can't do without, so the name you'd rather
not wrap gets its line and the rest wrap round it.

`.Hidden()` keeps a view's room, so nothing moves when it comes back. To take the room away
too, leave the view out with `null`, which a stack skips.

### Grids

A `Grid` of `GridRow`s lines its columns up, as a settings screen does with its labels. Each
column is as wide as its widest cell. A column with a cell that stretches, such as a
`Slider`, takes the room left over, and when there isn't enough room every column shrinks
alike. A child that isn't a row, such as a `Divider`, spans every column.

```csharp
new Grid(ColumnGap: 16f, RowGap: 12f)
{
    new GridRow { new Text("Music"), new Slider(music, setMusic), new Text($"{music:P0}") },
    new Divider(),
    new GridRow { new Text("Voice chat"), new Slider(voice, setVoice), new Text($"{voice:P0}") },
}
```

Give `Columns` and no rows for equal columns, filled left to right and then down, as an
inventory is. A `null` child leaves its cell empty. A row is as tall as its tallest cell.

### Flows and views that fit

A `FlowStack` puts its children in a row and starts a new line when the next one doesn't
fit, for tags, chips and keywords. Its `Alignment` sets where each line sits.

A `ViewThatFits` shows the first of its children that fits the room it's offered, so a label
can shorten to a word and then to an icon as its room shrinks. `new ViewThatFits(Axis.Horizontal)`
only checks the width. Only the child shown takes focus, and tests see only it.

### Lazy lists

A `LazyVStack` in a `ScrollView` makes only the rows near the screen, so a leaderboard of
10,000 players costs what a short one does:

```csharp
new ScrollView
{
    new LazyVStack<Player>(players, player => new PlayerRow(player), RowHeight: 36f, Spacing: 2f),
}.Frame(height: 300f)
```

Every row is `RowHeight` tall, so the stack knows its length without making them. It keeps
a screen's worth of rows made above and below what shows, so scrolling and moving focus
find their rows ready. Items name their rows, so they should differ from each other. Hand it
a new list when the items change, since a list changed in place looks the same as the last.
Outside a scroll view it makes every row.

## Containers

Containers hold other views and give them a shape a game screen often needs. You fill each
one with collection syntax, as you would a stack, and `null` leaves a spot empty.

![A card, a form with a disclosure group, a tab view and a navigation stack in the storybook](media/ui-containers.webp)

A `Card` is a raised panel on the theme's surface colour, for a quest, a character's stats
or a shop item. Its views stack down the leading edge, `Spacing` apart, `Inset` from its
edge. A `LabeledContent` puts a label at the start of a row and a value or a control at the
end, and text given as the value is muted:

```csharp
new Card
{
    new Text("Arin, the ranger").TextStyle(TextStyle.Headline),
    new LabeledContent("Health", $"{health} / {maxHealth}"),
    new LabeledContent("Difficulty", new Badge("Hard", BadgeStyle.Destructive)),
}
```

### Forms

A `Form` is a settings screen made of `Section`s, one under the other, filling the width it's
offered. A section draws its rows on a rounded panel with a line between each, under an
optional header and over an optional note. A row left out with `null` adds no line, and the
rows after it keep their state. Put the form in a `ScrollView` when it may be taller than the
screen.

```csharp
new Form
{
    new Section("Display")
    {
        new Toggle("Fullscreen", fullscreen, setFullscreen),
        new LabeledContent("Resolution", "2560 × 1440"),
    },
    new Section("Audio", "Music fades out in menus.")
    {
        new LabeledContent("Music", new Slider(music, setMusic).Frame(width: 180f)),
        new DisclosureGroup("Advanced") { new Toggle("Mono", mono, setMono) },
    },
}
```

A `DisclosureGroup` is a heading with a chevron that shows or hides the views under it. Left
alone it keeps whether it's open itself, starting closed. Give it `IsExpanded` and `OnChange`
to keep that in the game instead: it then shows what you pass and only asks to change. Tests
find its heading as `UiRole.Disclosure`, `Checked` while it's open.

### Tab views

A `TabView` shows one of its `Tab`s under a bar of tabs. Left alone it keeps the tab picked
itself, or give it `Selection` and `OnChange` to keep it in the game. The bar is one focus
stop, whose left and right switch tabs, and the gamepad's bumpers switch them from anywhere
on the screen, in the innermost tab view, without the game seeing the press. Under a popup
that traps, the bumpers only reach a tab view inside it. Tests find each tab as
`UiRole.Tab`, `Checked` when it's shown.

```csharp
new TabView
{
    new Tab("Video", videoSettings) { Icon = IconName.Info },
    new Tab("Audio", audioSettings),
}
```

### Navigation stacks

A `NavigationStack<T>` shows the page on top of a `NavigationPath<T>`, or its root when the
path is empty, under a bar with the page's title and a button back, named after the page
underneath. Escape and the gamepad's East button go back too, and at the root they're left to
the game. A stack inside a sheet goes back before the sheet closes, and a menu open over a
stack closes before the stack goes back. Keep the path in a field or a state so it lives
across frames:

```csharp
private readonly NavigationPath<SettingsPage> _path = new();

new NavigationStack<SettingsPage>(_path, "Settings", mainMenu, page => page switch
{
    SettingsPage.Graphics => graphicsPage,
    _ => audioPage,
})
```

`Push` opens a page, `Back` goes back one and says whether there was one, and `BackToRoot`
empties the path. A page's title is its `ToString`, or what `TitleOf` gives. Tests find the
stack as `UiRole.Navigation`, with the title shown as its `Label` and the depth as its
`Value`.

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

## Animation

The built-in controls move on their own. A switch's knob slides across, a slider's thumb eases
to where the track was pressed, menus drop from their buttons, sheets rise, and dialogs and
their scrims fade in. What a control says it's set to changes at once, so a test or a game
reading it never waits for the motion.

`UseAnimation` eases a number of your own toward a target. Give it the target every frame, and
it returns where the number is on its way there. Change the target and it sets off from where
it is, so a change halfway turns round smoothly:

```csharp
record HealthBar(float Health) : UiView
{
    public override UiView Body
    {
        get
        {
            float shown = UseAnimation(Health / 100f, 0.4f);
            return new ZStack(Alignment.Leading)
            {
                new Capsule().Fill(Color.Hex("#EF4444", 0.2f)),
                new Capsule().Fill(Color.Hex("#EF4444")).Frame(width: 320f * shown),
            }.Frame(320f, 10f);
        }
    }
}
```

It takes the seconds, 0.2 by default, an `Easing`, `Easing.Out` by default, and `from:` for where a new one starts, so
a view can move in from somewhere the first time it shows. While a number is on its way,
only that component's `Body` runs again each frame, and it stops once the number arrives.
`UseTime()` is the interface's time in seconds, for something that never stops, like a
spinner; its `Body` runs every frame for as long as it reads it, even under `.Hidden()`, so
leave a spinner out of the tree when nothing is loading.

`.Transition(...)` eases a view in when it appears: `Entrance.Fade`, `Rise` or `Drop`, over
the seconds you pass. It takes its full room from the first frame, so nothing around it
jumps, and it takes clicks all the way in. To play it again, give the view a new `.Id(...)`.

```csharp
new Text("Quest complete").Transition(Entrance.Rise, 0.4f)
```

`.Animations(false)` turns motion off for everything inside, for a player who wants less of
it: controls jump straight to where they're going, `UseAnimation` returns its target, and
`UseTime()` stays at zero, so nothing runs again each frame. Read `Environment.Animates` in
a view of your own to do the same.

The interface's clock is the frame's, which moves on with each tick in a headless run. Tick
past the motion to see where things end up, and `run.Animating` says whether anything is
still moving:

```csharp
run.Input.Click("music");
run.Ticks(1);
Assert.True(run.Element("music").Checked);   // at once
Assert.True(run.Animating);                  // the knob is on its way
run.Ticks(10);
Assert.False(run.Animating);
```

## Sounds

The controls make small sounds, as game menus do: a tick when the pointer comes onto a button or
focus moves, a click when something is pressed, a softer one going back, a blip when a popup
opens by itself, a step as left and right change a slider, a stepper or a picker, and a low buzz
for a press on something disabled. A popup that a press opens is heard as the press, so the two
don't play on top of each other. The stock sounds are made in code, so a game has them before it
has any audio files.

They come from the theme's `Sounds`, one for each of `Hover`, `Focus`, `Press`, `Back`, `Open`,
`Denied`, `Step` and `Confirm`. `UiSounds.None` is silent. Give a sound of your own with
`UiSound.File`, and leave one null to keep that one quiet:

```csharp
var theme = Theme.Dark with
{
    Sounds = UiSounds.Default with
    {
        Press = UiSound.File("sounds/click.wav") with { Volume = 0.6f },
        Hover = null,
    },
};
```

They play flat on the `Interface` channel, so `Audio.Volume(Channel.Interface, 0.5f)` sets how
loud all of them are, and a settings screen can offer that as its own slider. Each sound's pitch
moves a few percent each time, `PitchVariation`, so one heard over and over doesn't tire. In a
headless run they show in `run.Sounds` with names such as `interface/press`.

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

`.Draggable(payload)` drags with the left button. To let other buttons drag, or carry something
different for each, hand it a function instead. It's told how the drag began and returns what to
carry, or null when that press shouldn't drag. It runs at the press, so it only chooses; the drop
is where the game changes. The drag ends when that button is let go, and other buttons pressed
meanwhile do nothing. A left press that turns into a drag is no longer a click, but a left click
already held on something else still clicks when it's let go, even if another button dragged in
between:

```csharp
new Pressable(look => Cell(stack))
    .Draggable(start => start.Button switch
    {
        MouseButton.Middle => new Carry(cell, stack.Count / 2),
        MouseButton.Left => new Carry(cell, start.Shift ? 1 : stack.Count),
        _ => null,
    }, carry => Icon(carry))
    .DropTarget<Carry>(carry => bag.Move(carry, cell));
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
the interface, `input.Mouse.OverUi` is true, so a game can leave those clicks alone. To
have a panel let clicks through to the game, as a stats readout should, use
`.IgnoresPointer()`.

A `ListView` compares its list by reference, so hand it a new one when the items change.

[The crafting bench](../examples/Crafting/CraftingGame.cs) uses all of this.

## Touch

On a touch screen, the interface works with a finger as it does with the mouse, and nothing
needs to change to support it:

| Finger | What it does |
| --- | --- |
| A tap | Clicks what's under it, as a click would. A text field it lands on takes focus |
| A drag on a `ScrollView` | Moves it with the finger, and a quick one flings it on until it slows |
| A drag on a slider | Follows the finger |
| Holding still for half a second | Runs `.OnLongPress(...)`, or opens a `.ContextMenu(...)` where the finger is |

A finger shows what it holds as pressed while it stays on it, and lifting it off there is no tap,
just as with a button. Once a finger has moved more than a few pixels, the drag goes to the
nearest scroll view that runs the way it mostly moved, so a strip that scrolls sideways inside a
list that scrolls down takes sideways drags and leaves the rest to the list. A drag never taps.
A finger on a list that's still flinging stops it. A finger never hovers, so tooltips stay with
the mouse.

`.OnLongPress(...)` is for what a right click or a held button does with the mouse, such as
pinning a quest or showing more about an item. It runs on the tick the half second is up, while
the finger is still down, and the lift after it doesn't tap. A view that's turned off ignores it:

```csharp
new QuestRow(quest)
    .OnClick(() => journal.Pick(quest))
    .OnLongPress(() => journal.Pin(quest));
```

The interface takes the first finger that lands on it. Fingers that land elsewhere, and a second
finger while the first holds a control, go to the game in `input.Touches`. Each one has an `Id`
that stays the same until it lifts, where it is, how far it moved this tick, and whether it came
down or lifted since the last tick. A finger that lifted is in the list once more, marked
`Released`:

```csharp
foreach (var touch in input.Touches)
{
    if (touch.Pressed)
    {
        aim.Start(touch.Id, touch.Position);
    }
    else if (touch.Released)
    {
        aim.Fire(touch.Id);
    }
    else
    {
        aim.Move(touch.Id, touch.Delta);
    }
}
```

![Quests flung down the list, one picked with a tap and one pinned with a long press](media/ui-touch.webp)

Touch screens are read on Windows. Scripts and tests can touch on any system, with `TouchDown`,
`TouchMove`, `TouchUp`, `Tap` and `Swipe`, as [Testing](testing.md) shows.

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
`input.Mouse.Position` stay in pixels. A headless test sets the scale with `run.UiScale`.

On a Mac's Retina screen a window has two pixels for each point of its size, so a game in a
window multiplies the scale by that, and the interface looks the same size as on any other
screen. Screenshots and videos have the pixels `--size` asks for, so they keep the scale.

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

## Fonts

The default font is [Inter](https://rsms.me/inter/), built into `Super.dll`, so text
works with no files around. A game's own font is cooked from a `.ttf` or `.otf` file and
named through [`content.Fonts`](content.md#fonts), and `.Font(font)` puts it on text here and
everywhere inside, as `.Bold()` does:

```csharp
GameFont heading = content.Fonts.Font("fonts/pirata-one.ttf");
GameFont body = content.Fonts.Font("fonts/figtree.ttf", bold: "fonts/figtree-bold.ttf");

new VStack(
    new Text("The Drowned Bell").Font(heading).FontSize(28f),
    new Text("+3 Armor").Bold(),
    new Text("Found on the third floor")
).Font(body);
```

The built-in Inter has Regular, Medium, SemiBold and Bold weights, and an italic. A font of your
own has a regular and a bold file: `.Bold()`, `SemiBold` and the text styles that use them
draw in the bold file, and `Medium` in the regular one. A font named without a bold file has
no bold, and bold text in it is drawn in the regular weight. Italic is only built in. Every font and size shares one 1024-pixel sheet of
characters; a character drawn once there's no room left takes its place in the line but isn't
seen, so keep to a few fonts and sizes.

## Showing how the game runs

Every game has a stats panel built in. Press F3 in the window to show or hide it (a game
can [pick another key or none](#the-stats-key)), or start
with it on with `--stats` from [the command line](command-line.md), which works with
`--screenshot` and `--record` too. It sits in the top right corner over the game's interface
and never takes the mouse.

![The Courtyard without the panel and with it](media/render-stats.webp)

From the top, the panel shows:

- The frame rate and how long a frame takes.
- CPU: how long a frame kept the game's thread busy, which is the frame less the time spent
  waiting for the GPU and the display.
- GPU: how long a frame kept the GPU busy, with the five slowest passes under it. A GPU that
  can't time its passes leaves these out.
- Draw calls: the models drawn in the newest frame, over every pass, shadows included.
- Triangles: what the camera drew after culling and detail, and under it what the shadows
  drew. A still light's kept shadow isn't drawn again, so it isn't counted.
- Shadow tiles: the point and spot light shadows drawn this frame and those kept from
  earlier ones, when the scene has any.
- GPU memory: what the renderer uses of what it has taken from the device. It takes memory
  in 64 MB blocks, so the second figure is the larger.
- Heap: the size of the garbage collected heap and the bytes allocated a frame on the
  game's thread.
- Collections: garbage collections since the game started.
- The frame's size and the GPU's name.

The times are averages over half a second, and the counts are the newest frame's. Timing
the GPU costs a little, so the renderer is only measured while the panel is shown.

### The stats key

The key is `GameStats.ToggleKey`, F3 unless the game picks another. The engine keeps that
key, so the game never sees it, and any other key, F3 included once it isn't the toggle,
reaches the game as usual. Set it to null to turn the key off, as a shipped game might;
`Shown` and `--stats` still work then. Either can change while the game runs, and a game
can show the panel from a menu of its own by setting `Shown`:

```csharp
class DebugMenu
{
    private readonly GameStats _stats;

    public DebugMenu(GameStats stats)
    {
        _stats = stats;
        _stats.ToggleKey = Key.F10;
    }

    public void ShowStats(bool on) => _stats.Shown = on;

    public void LockForRelease() => _stats.ToggleKey = null;
}
```

### Your own panel

To draw your own panel instead, ask for `GameStats` in your constructor and read `Latest`,
which updates twice a second. It holds the frame rate, frame time, bytes allocated per frame
on the game's thread, heap size and garbage collections, and in `Render` the renderer's
figures above, while `Shown` is on. Work done for the interface isn't counted, so a stats
panel doesn't report itself. Reef's panel is in
[StatsPanel.cs](../examples/Reef/StatsPanel.cs).

These numbers come from the real clock, so only show them. If they reach `Update`, a seed
stops repeating.

## Not yet

Touch is read on Windows only, and only tried with scripts so far, not on a real touch screen.
A finger can't drag and drop, there's no pinch or rotate, and a long press is for fingers only.
Text fields are one line only. Menus have no submenus, a context menu has no key to open it from
the keyboard or the gamepad, toasts have no buttons of their own, and views ease in when they
appear but leave at once: there are no exit transitions, and no transition that slides a whole
page across. Animations ease numbers and come in fixed entrances; there are no springs or
keyframes. A lazy stack's rows all have one height, there's no lazy row or lazy
grid, and when a lazy list scrolls far in one frame the new rows show a frame later.
Going back in a navigation stack forgets the state of the page that was under it, and a tab
view or a navigation stack under `.Hidden()` still takes the bumpers and Escape. There's no
key on the keyboard that switches tabs from anywhere, as the bumpers do. Nothing plays the
`Confirm` sound yet.
