using System.Numerics;
using Super;
using Super.Ui;

namespace StorybookExample;

/// <summary>
/// Whole screens a game would have, built only from the elements in the other stories: a title screen, a loading
/// screen, settings, an inventory, a main menu, a heads-up display and a character creator. Each works: its controls
/// keep what they're set to.
/// </summary>
internal static class Showcases
{
    public static UiView TitleScreen() => new GameScreen(new TitleScreen(), Scenery.Dusk, Padded: false);

    public static UiView Loading() => new GameScreen(new LoadingScreen(), Scenery.Dusk, Padded: false);

    public static UiView Settings() => new GameScreen(new SettingsScreen());

    public static UiView Inventory() => new GameScreen(new InventoryScreen());

    public static UiView MainMenu() => new GameScreen(new MainMenuScreen(), Scenery.Dusk);

    public static UiView Hud() => new GameScreen(new HudScreen(), Scenery.World, Padded: false);

    public static UiView CharacterCreator() => new GameScreen(new CreatorScreen());
}

/// <summary>A key or a pad button, whichever the player last used.</summary>
internal sealed record DevicePrompt(Binding Key, Binding Pad) : UiView
{
    public override UiView Body => new ButtonPrompt(Environment.Device == InputDevice.Gamepad ? Pad : Key);
}

/// <summary>A key or pad button to press and what it does, as a footer shows them.</summary>
internal sealed record Hint(Binding Key, Binding Pad, string Action) : UiView
{
    public override UiView Body => new HStack(Spacing: 6f)
    {
        new DevicePrompt(Key, Pad),
        new Text(Action).TextStyle(TextStyle.Footnote).Foreground(Tone.Muted),
    };
}

/// <summary>Small pieces the showcase screens share: bars and item pictures.</summary>
internal static class ShowcaseParts
{
    /// <summary>A bar filled this far in a colour, for health and stamina, where what's lost drains away after it.</summary>
    public static UiView Bar(float fraction, Color color, float width) => new TrailingBar(fraction, color, width);

    /// <summary>A square with an item's first letter, tinted by how rare it is, standing in for its picture.</summary>
    public static UiView ItemPicture(string name, Color rarity, float side) =>
        new Text(name[..1])
            .TextStyle(TextStyle.Title3)
            .Foreground(rarity.Mix(Color.White, 0.45f))
            .Frame(side, side)
            .Background(Paint.Radial(rarity with { A = 0.35f }, rarity with { A = 0.08f }))
            .CornerRadius(6f);
}

/// <summary>A bar that eases to its fill, with a pale trail of what it lost that drains a moment later.</summary>
internal sealed record TrailingBar(float Fraction, Color Color, float Width) : UiView
{
    public override UiView Body
    {
        get
        {
            float fill = Math.Clamp(Fraction, 0f, 1f);
            float shown = UseAnimation(fill, 0.25f, Easing.Out);
            float trail = MathF.Max(UseAnimation(fill, 0.9f, Easing.InOut), shown);
            return new ZStack(Alignment.Leading)
            {
                new Capsule().Fill(Color with { A = 0.25f }),
                new Capsule().Fill(Color.White with { A = 0.6f }).Frame(width: Width * trail),
                new Capsule().Fill(Color).Frame(width: Width * shown),
            }.Frame(Width, 8f);
        }
    }
}

/// <summary>What a showcase screen is drawn over.</summary>
internal enum Scenery
{
    /// <summary>The theme's background, as a menu over a paused game would be.</summary>
    Plain,

    /// <summary>A sky at dusk over dark hills, for a title screen.</summary>
    Dusk,

    /// <summary>Hills under a sky, standing in for the game behind a heads-up display.</summary>
    World,
}

/// <summary>A showcase in a frame the size of a small game window, over its backdrop.</summary>
internal sealed record GameScreen(UiView Content, Scenery Backdrop = Scenery.Plain, bool Padded = true) : UiView
{
    private const float Width = 960f;
    private const float Height = 600f;

    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            var inside = (Padded ? Content.Padding(32f) : Content).Frame(width: Width, height: Height, alignment: Alignment.TopLeading);
            var behind = Backdrop switch
            {
                Scenery.Dusk => inside
                    .Background(Hills(Color.Hex("#1E1B4B"), Color.Hex("#14112E")))
                    .Background(Stars())
                    .Background(Paint.Linear(Color.Hex("#312E81"), Color.Hex("#7C3A5C"))),
                Scenery.World => inside
                    .Background(Hills(Color.Hex("#4F7A4A"), Color.Hex("#3E6B3F")))
                    .Background(Paint.Linear(Color.Hex("#7DB4E0"), Color.Hex("#D8E9F0"))),
                _ => inside.Background(theme.Colors.Background),
            };
            return behind
                .CornerRadius(theme.RadiusLarge)
                .Border(1f, theme.Colors.Border)
                .Shadow(ShadowSize.Large);
        }
    }

    /// <summary>Two hills along the bottom, cut off by the screen's corners.</summary>
    private static UiView Hills(Color far, Color near) => new ZStack(Alignment.Bottom)
    {
        new Capsule().Fill(far).Frame(1400f, 420f).Offset(-260f, 240f),
        new Capsule().Fill(near).Frame(1200f, 360f).Offset(380f, 230f),
    }.Frame(width: Width, height: Height);

    /// <summary>A scatter of stars in the upper sky, the same every time.</summary>
    private static UiView Stars()
    {
        var sky = new ZStack(Alignment.TopLeading);
        var random = new Random(7);
        for (int i = 0; i < 40; i++)
        {
            float side = 1.5f + (float)random.NextDouble() * 1.5f;
            var at = new Vector2((float)random.NextDouble() * Width, (float)random.NextDouble() * Height * 0.55f);
            sky.Add(new Circle().Fill(Color.White with { A = 0.35f + (float)random.NextDouble() * 0.5f }).Frame(side, side).Offset(at.X, at.Y));
        }

        return sky.Frame(width: Width, height: Height, alignment: Alignment.TopLeading);
    }
}

/// <summary>A main menu: the game's name, a column of choices the d-pad goes round, and a quit that asks first.</summary>
internal sealed record MainMenuScreen : UiView
{
    private static readonly Color Pale = Color.Hex("#C7D2FE");

    public override UiView Body
    {
        get
        {
            var quitting = UseState(false);
            var said = UseState("Pick something to start.");
            var quit = new Dialog("Quit to the desktop?", "Your last save was 12 minutes ago.")
            {
                Buttons =
                [
                    new DialogButton("Quit", () => said.Value = "You quit.", DialogRole.Destructive),
                    new DialogButton("Cancel", () => said.Value = "You stayed.", DialogRole.Cancel),
                ],
            };
            return new VStack(HorizontalAlignment.Leading, 0f)
            {
                new Spacer(),
                new Text("EMBERFALL").FontSize(56f).Weight(FontWeight.Bold).Foreground(Color.Hex("#F8FAFC")).Transition(Entrance.Drop, 0.5f),
                new Text("The last of the lamplighters").Foreground(Pale).Padding(0f, 0f, 28f, 2f),
                Choices(said, quitting.Value, () => quitting.Value = true, () => quitting.Value = false, quit),
                new Spacer(),
                new HStack(Spacing: 20f)
                {
                    new Text(said.Value).TextStyle(TextStyle.Footnote).Foreground(Pale).Id("menu-said"),
                    new Spacer(),
                    new Hint(Key.Enter, GamepadButton.South, "Select"),
                    new Hint(Key.Escape, GamepadButton.East, "Back"),
                    new Text("v0.4.2").TextStyle(TextStyle.Footnote).Foreground(Color.Hex("#818CF8")),
                },
            }
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.Leading)
            .Theme(Theme.Dark);
        }
    }

    private static UiView Choices(State<string> said, bool quitting, Action ask, Action dismiss, Dialog quit) =>
        new VStack(HorizontalAlignment.Leading, 2f)
        {
            Choice("Continue", "Chapter 4 · The flooded mill", () => said.Value = "Continuing chapter 4.").Id("continue"),
            Choice("New game", null, () => said.Value = "A new game.").Id("new-game"),
            Choice("Load game", null, () => said.Value = "Loading.").Id("load-game"),
            Choice("Settings", null, () => said.Value = "Settings.").Id("menu-settings"),
            Choice("Quit", null, ask).ConfirmationDialog(quitting, dismiss, quit).Id("menu-quit"),
        }.FocusWraps().DefaultFocus().Transition(Entrance.Rise, 0.4f);

    private static UiView Choice(string label, string? note, Action press)
    {
        var button = new Button(label, press) { Style = ButtonStyle.Menu, FullWidth = true }.ControlSize(ControlSize.Large).Frame(width: 300f);
        return note is null
            ? button
            : new HStack(Spacing: 16f) { button, new Text(note).TextStyle(TextStyle.Footnote).Foreground(Pale) };
    }
}

/// <summary>
/// A heads-up display over the game: health and stamina, gold that counts, a compass, a minimap, a quest, a hotbar,
/// a subtitle, and a chest to open that has a trap in it.
/// </summary>
internal sealed record HudScreen : UiView
{
    private static readonly string[] Hotbar = ["Sword", "Bow", "Potion", "Torch", "Map", "Rope"];
    private static readonly Color Gold = Color.Hex("#FBBF24");

    private static readonly SubtitleLine Said = new("The gate's barred from inside. There must be another way.", 0f, 1f)
    {
        Speaker = "Arin",
        SpeakerColor = Color.Hex("#FDE68A"),
    };

    public override UiView Body
    {
        get
        {
            var picked = UseState(0);
            var opened = UseState(0);
            int chests = opened.Value;
            return new Spacer()
                .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity)
                .Overlay(Vitals(chests).Padding(20f), Alignment.TopLeading)
                .Overlay(Wayfinding().Padding(20f), Alignment.Top)
                .Overlay(Map().Padding(20f), Alignment.TopTrailing)
                .Overlay(new Circle().Stroke(Color.White with { A = 0.8f }, 1.5f).Frame(10f, 10f))
                .Overlay(new VStack(Spacing: 12f) { new Subtitles(Said) { Size = 18f }, Slots(picked.Value, slot => picked.Value = slot) }.Padding(20f), Alignment.Bottom)
                .Overlay(Prompt(() => opened.Value++).Padding(24f), Alignment.BottomTrailing)
                .Overlay(new VStack { chests > 0 ? new Achievement("Light fingers") { Description = "Open a chest. Mind the needle." }.Id($"chest-{chests}") : null }.Padding(96f, 0f, 0f, 0f), Alignment.Top)
                .Theme(Theme.Dark);
        }
    }

    private static UiView Panel(UiView content) => content
        .Padding(14f, 12f)
        .Background(Theme.Dark.Colors.Surface with { A = 0.82f })
        .CornerRadius(Theme.Dark.Radius + 2f);

    // Each chest gives 25 gold, and its needle takes a fifth of the health.
    private static UiView Vitals(int chests) => Panel(new VStack(HorizontalAlignment.Leading, 8f)
    {
        new HStack(Spacing: 8f)
        {
            new Text("Arin").TextStyle(TextStyle.Headline),
            new Badge("Lv 12", BadgeStyle.Secondary),
            new Spacer(),
            new Icon(IconName.Circle).Foreground(Gold).Frame(10f, 10f),
            new CountingText(120 + chests * 25).Foreground(Gold).MonospacedDigit().Id("gold"),
        },
        ShowcaseParts.Bar(MathF.Max(0.72f - chests * 0.2f, 0.04f), Color.Hex("#EF4444"), 200f).Semantics(UiRole.ProgressBar, "Health"),
        ShowcaseParts.Bar(0.45f, Color.Hex("#F59E0B"), 200f).Semantics(UiRole.ProgressBar, "Stamina"),
    }.Frame(width: 200f));

    private static UiView Wayfinding() => new Compass(0.3f)
    {
        Marks = [new CompassMark(0.6f, Gold, "Keep"), new CompassMark(-0.8f, Color.Hex("#5BA8F5"))],
    }.Frame(width: 320f);

    private static UiView Map() => new VStack(HorizontalAlignment.Trailing, 12f)
    {
        new MinimapFrame(Ground()) { Size = 120f, Heading = 0.3f, Marks = [new MinimapMark(new Vector2(0.5f, -0.6f), Gold)] },
        Quest(),
    };

    // A painted map: grass, a road and the moat round the keep.
    private static UiView Ground() => new ZStack
    {
        new Rectangle().Fill(Color.Hex("#4E7A45")),
        new Rectangle().Fill(Color.Hex("#C9B48A")).Frame(width: 14f, height: 400f).Offset(10f, 0f),
        new Ellipse().Stroke(Color.Hex("#3D7CB8"), 6f).Frame(width: 70f, height: 60f).Offset(40f, -50f),
    };

    private static UiView Quest() => Panel(new VStack(HorizontalAlignment.Leading, 6f)
    {
        new Text("The Ember Crown").TextStyle(TextStyle.Headline),
        Step("Reach the burning keep", done: true),
        Step("Find a way past the gate", done: false),
        Step("Bring back the crown", done: false),
    }.Frame(width: 220f, alignment: Alignment.Leading));

    private static UiView Step(string text, bool done) => new HStack(Spacing: 8f)
    {
        new Icon(done ? IconName.Check : IconName.Dot).Foreground(done ? Tone.Success : Tone.Muted),
        new Text(text).TextStyle(TextStyle.Subheadline).Foreground(done ? Tone.Muted : Tone.Default),
    };

    private static UiView Slots(int picked, Action<int> pick)
    {
        var row = new HStack(Spacing: 6f);
        for (int i = 0; i < Hotbar.Length; i++)
        {
            row.Add(Slot(i, i == picked, pick));
        }

        return Panel(row);
    }

    private static UiView Slot(int index, bool picked, Action<int> pick) => new Pressable(
        _ => ShowcaseParts.ItemPicture(Hotbar[index], picked ? Gold : Color.Hex("#A1A1AA"), 48f)
            .Overlay(new Text($"{index + 1}").TextStyle(TextStyle.Caption).Foreground(Tone.Muted).Padding(3f, 5f), Alignment.TopLeading)
            .Border(picked ? 2f : 1f, picked ? Gold : Theme.Dark.Colors.Border)
            .CornerRadius(6f)
            .Scale(picked ? 1.08f : 1f),
        () => pick(index))
        .Semantics(UiRole.Option, Hotbar[index], isChecked: picked)
        .Id($"hotbar-{index + 1}");

    private static UiView Prompt(Action open) => new Pressable(
        _ => Panel(new HStack(Spacing: 8f) { new DevicePrompt(Key.E, GamepadButton.West), new Text("Open the chest") }),
        open)
        .Semantics(UiRole.Button, "Open the chest")
        .Id("open-chest");
}
