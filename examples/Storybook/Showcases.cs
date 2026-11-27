using Super;
using Super.Ui;

namespace StorybookExample;

/// <summary>
/// Whole screens a game would have, built only from the elements in the other stories: settings, an inventory,
/// a main menu, a heads-up display and a character creator. Each works: its controls keep what they're set to.
/// </summary>
internal static class Showcases
{
    public static UiView Settings() => new GameScreen(new SettingsScreen());

    public static UiView Inventory() => new GameScreen(new InventoryScreen());

    public static UiView MainMenu() => new GameScreen(new MainMenuScreen(), Scenery.Dusk);

    public static UiView Hud() => new GameScreen(new HudScreen(), Scenery.World, Padded: false);

    public static UiView CharacterCreator() => new GameScreen(new CreatorScreen());
}

/// <summary>Small pieces the showcase screens share: key hints, bars and item pictures.</summary>
internal static class ShowcaseParts
{
    /// <summary>A key to press and what it does, as a footer shows them.</summary>
    public static UiView Hint(string key, string action) => new HStack(Spacing: 6f)
    {
        new KeyHint(key),
        new Text(action).TextStyle(TextStyle.Footnote).Foreground(Tone.Muted),
    };

    /// <summary>A bar filled this far in a colour, for health and stamina.</summary>
    public static UiView Bar(float fraction, Color color, float width) => new ZStack(Alignment.Leading)
    {
        new Capsule().Fill(color with { A = 0.25f }),
        new Capsule().Fill(color).Frame(width: width * Math.Clamp(fraction, 0f, 1f)),
    }.Frame(width, 8f);

    /// <summary>A square with an item's first letter, tinted by how rare it is, standing in for its picture.</summary>
    public static UiView ItemPicture(string name, Color rarity, float side) =>
        new Text(name[..1])
            .TextStyle(TextStyle.Title3)
            .Foreground(rarity.Mix(Color.White, 0.45f))
            .Frame(side, side)
            .Background(Paint.Radial(rarity with { A = 0.35f }, rarity with { A = 0.08f }))
            .CornerRadius(6f);
}

/// <summary>What a showcase screen is drawn over.</summary>
internal enum Scenery
{
    /// <summary>The theme's background, as a menu over a paused game would be.</summary>
    Plain,

    /// <summary>A sky at dusk, for a title screen.</summary>
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
                Scenery.Dusk => inside.Background(Paint.Linear(Color.Hex("#312E81"), Color.Hex("#0B1020"))),
                Scenery.World => inside.Background(Hills()).Background(Paint.Linear(Color.Hex("#7DB4E0"), Color.Hex("#D8E9F0"))),
                _ => inside.Background(theme.Colors.Background),
            };
            return behind
                .CornerRadius(theme.RadiusLarge)
                .Border(1f, theme.Colors.Border)
                .Shadow(ShadowSize.Large);
        }
    }

    /// <summary>Two hills along the bottom, cut off by the screen's corners.</summary>
    private static UiView Hills() => new ZStack(Alignment.Bottom)
    {
        new Capsule().Fill(Color.Hex("#4F7A4A")).Frame(1400f, 420f).Offset(-260f, 240f),
        new Capsule().Fill(Color.Hex("#3E6B3F")).Frame(1200f, 360f).Offset(380f, 230f),
    }.Frame(width: Width, height: Height);
}

/// <summary>A title screen: the game's name, a column of choices, and a quit that asks first.</summary>
internal sealed record MainMenuScreen : UiView
{
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
                new Text("EMBERFALL").TextStyle(TextStyle.LargeTitle).FontSize(56f).Foreground(Color.Hex("#F8FAFC")),
                new Text("The last of the lamplighters").Foreground(Color.Hex("#C7D2FE")).Padding(0f, 0f, 28f, 2f),
                Choices(said, quitting.Value, () => quitting.Value = true, () => quitting.Value = false, quit),
                new Spacer(),
                new HStack(Spacing: 20f)
                {
                    new Text(said.Value).TextStyle(TextStyle.Footnote).Foreground(Color.Hex("#C7D2FE")).Id("menu-said"),
                    new Spacer(),
                    ShowcaseParts.Hint("Enter", "Select"),
                    ShowcaseParts.Hint("Esc", "Back"),
                    new Text("v0.4.2").TextStyle(TextStyle.Footnote).Foreground(Color.Hex("#818CF8")),
                },
            }
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.Leading)
            .Theme(Theme.Dark);
        }
    }

    private static UiView Choices(State<string> said, bool quitting, Action ask, Action dismiss, Dialog quit) =>
        new VStack(HorizontalAlignment.Leading, 8f)
        {
            Choice("Continue", "Chapter 4 · The flooded mill", () => said.Value = "Continuing chapter 4.", ButtonStyle.Default).DefaultFocus().Id("continue"),
            Choice("New game", null, () => said.Value = "A new game.", ButtonStyle.Secondary).Id("new-game"),
            Choice("Load game", null, () => said.Value = "Loading.", ButtonStyle.Secondary).Id("load-game"),
            Choice("Settings", null, () => said.Value = "Settings.", ButtonStyle.Secondary).Id("menu-settings"),
            Choice("Quit", null, ask, ButtonStyle.Ghost).ConfirmationDialog(quitting, dismiss, quit).Id("menu-quit"),
        }.Transition(Entrance.Rise, 0.4f);

    private static UiView Choice(string label, string? note, Action press, ButtonStyle style)
    {
        var button = new Button(label, press) { Style = style, FullWidth = true }.Frame(width: 260f).ControlSize(ControlSize.Large);
        return note is null
            ? button
            : new HStack(Spacing: 16f) { button, new Text(note).TextStyle(TextStyle.Footnote).Foreground(Color.Hex("#C7D2FE")) };
    }
}

/// <summary>A heads-up display over the game: health and stamina, a quest, a hotbar, and what to press nearby.</summary>
internal sealed record HudScreen : UiView
{
    private static readonly string[] Hotbar = ["Sword", "Bow", "Potion", "Torch", "Map", "Rope"];

    public override UiView Body
    {
        get
        {
            var picked = UseState(0);
            return new Spacer()
                .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity)
                .Overlay(Vitals().Padding(20f), Alignment.TopLeading)
                .Overlay(Quest().Padding(20f), Alignment.TopTrailing)
                .Overlay(new Circle().Stroke(Color.White with { A = 0.8f }, 1.5f).Frame(10f, 10f))
                .Overlay(Slots(picked.Value, slot => picked.Value = slot).Padding(20f), Alignment.Bottom)
                .Overlay(Prompt().Padding(24f), Alignment.BottomTrailing)
                .Theme(Theme.Dark);
        }
    }

    private static UiView Panel(UiView content) => content
        .Padding(14f, 12f)
        .Background(Theme.Dark.Colors.Surface with { A = 0.82f })
        .CornerRadius(Theme.Dark.Radius + 2f);

    private static UiView Vitals() => Panel(new VStack(HorizontalAlignment.Leading, 8f)
    {
        new HStack(Spacing: 8f) { new Text("Arin").TextStyle(TextStyle.Headline), new Badge("Lv 12", BadgeStyle.Secondary) },
        ShowcaseParts.Bar(0.72f, Color.Hex("#EF4444"), 200f).Semantics(UiRole.ProgressBar, "Health"),
        ShowcaseParts.Bar(0.45f, Color.Hex("#F59E0B"), 200f).Semantics(UiRole.ProgressBar, "Stamina"),
    });

    private static UiView Quest() => Panel(new VStack(HorizontalAlignment.Leading, 6f)
    {
        new Text("The Ember Crown").TextStyle(TextStyle.Headline),
        Step("Reach the burning keep", done: true),
        Step("Find a way past the gate", done: false),
        Step("Bring back the crown", done: false),
    }.Frame(width: 240f, alignment: Alignment.Leading));

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
        _ => ShowcaseParts.ItemPicture(Hotbar[index], picked ? Color.Hex("#FBBF24") : Color.Hex("#A1A1AA"), 48f)
            .Overlay(new Text($"{index + 1}").TextStyle(TextStyle.Caption).Foreground(Tone.Muted).Padding(3f, 5f), Alignment.TopLeading)
            .Border(picked ? 2f : 1f, picked ? Color.Hex("#FBBF24") : Theme.Dark.Colors.Border)
            .CornerRadius(6f),
        () => pick(index))
        .Semantics(UiRole.Option, Hotbar[index], isChecked: picked)
        .Id($"hotbar-{index + 1}");

    private static UiView Prompt() => Panel(new HStack(Spacing: 8f)
    {
        new KeyHint("E"),
        new Text("Open the chest"),
    });
}
