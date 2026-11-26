using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The stories for what opens over the page: menus, dialogs, sheets, popovers and toasts.</summary>
internal static class Popping
{
    private static readonly PickerOption<string>[] Resolutions =
    [
        new("1280 × 720", "720p"),
        new("1920 × 1080", "1080p"),
        new("2560 × 1440", "1440p"),
        new("3840 × 2160", "2160p"),
    ];

    public static UiView Menus() => Page(
        new Specimen(
            "A menu",
            "Click it, or press Enter on it, and pick a row. Escape, East or a click outside closes it, and the arrows move through it.",
            new Live<string>("nothing yet", (picked, set) => new VStack(HorizontalAlignment.Leading, 12f)
            {
                new HStack(Spacing: 8f)
                {
                    new Menu("Game", GameItems(set)) { Icon = IconName.Menu }.Id("game-menu"),
                    new Menu("Locked", []) { Disabled = true },
                },
                new Text($"Picked: {picked}.").Foreground(Tone.Muted),
            })),
        new Specimen(
            "A picker as a menu",
            "The button shows the picked one, and the menu checks it. It takes the least room of the picker styles.",
            new Live<string>("1080p", (resolution, set) => new HStack(Spacing: 16f)
            {
                new Text("Resolution").Frame(width: 120f, alignment: Alignment.Leading),
                new Picker<string>("Resolution", resolution, Resolutions, set) { Style = PickerStyle.Menu }.Id("resolution"),
            })),
        new Specimen(
            "A context menu",
            "Right-click the sword, or hold a finger on it, and the menu opens where it was.",
            new Live<string>("nothing yet", (done, set) => new VStack(HorizontalAlignment.Leading, 12f)
            {
                Sword(set),
                new Text($"The sword was {done}.").Foreground(Tone.Muted),
            })));

    /// <summary>An item card with a context menu.</summary>
    private static UiView Sword(Action<string> set) => new VStack(HorizontalAlignment.Leading, 4f)
        {
            new Label("Iron sword", IconName.Plus).TextStyle(TextStyle.Headline),
            new Text("Damage 12 · Weight 3").Foreground(Tone.Muted),
        }
        .Padding(16f)
        .Frame(width: 260f, alignment: Alignment.Leading)
        .Background(Color.Hex("#78716C") with { A = 0.18f })
        .CornerRadius(8f)
        .ContextMenu(
        [
            new MenuItem("Equip", () => set("equipped")) { Icon = IconName.ArrowUp },
            new MenuItem("Compare", () => set("compared")) { Icon = IconName.Search },
            MenuItem.Divider,
            new MenuItem("Drop", () => set("dropped")) { Icon = IconName.Close, Destructive = true },
        ])
        .Id("sword");

    public static UiView Dialogs() => Page(
        new Specimen(
            "An alert",
            "It needs an answer: a click outside does nothing, and Enter picks the focused button. Escape and East press Cancel.",
            new AlertDemo()),
        new Specimen(
            "A confirmation dialog",
            "A choice of actions from the bottom of the screen, with Cancel apart. A click outside cancels it.",
            new QuitDemo()));

    public static UiView Sheets() => Page(
        new Specimen(
            "A sheet",
            "A card in the middle of the screen over a scrim, for a form or a longer choice. A click on the scrim, Escape or East closes it.",
            new SheetDemo()),
        new Specimen(
            "A popover",
            "A small card under the view it hangs off, or above it when there's no room below.",
            new PopoverDemo()));

    public static UiView ToastStory() => Page(
        new Specimen(
            "Notes in the corner",
            "Each shows in the bottom corner for four seconds, and a click takes it away sooner. Three show at once.",
            new ToastDemo()));

    private static MenuItem[] GameItems(Action<string> set) =>
    [
        new MenuItem("New game", () => set("new game")) { Icon = IconName.Plus },
        new MenuItem("Save", () => set("save")) { Shortcut = "Ctrl+S" },
        new MenuItem("Load", () => set("load")) { Shortcut = "Ctrl+L" },
        new MenuItem("Load the autosave") { Disabled = true },
        MenuItem.Divider,
        new MenuItem("Quit", () => set("quit")) { Destructive = true },
    ];
}

/// <summary>A button that asks before it deletes a save, and one with only an OK.</summary>
internal sealed record AlertDemo : UiView
{
    public override UiView Body
    {
        get
        {
            var asking = UseState(false);
            var informing = UseState(false);
            var said = UseState("nothing yet");
            var delete = new Dialog("Delete this save?", "Slot 2 · Chapter 4 · 6 h 12 min. You can't undo this.")
            {
                Buttons =
                [
                    new DialogButton("Cancel", () => said.Value = "kept", DialogRole.Cancel),
                    new DialogButton("Delete", () => said.Value = "deleted", DialogRole.Destructive),
                ],
            };
            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                new HStack(Spacing: 8f)
                {
                    new Button("Delete save", () => asking.Value = true) { Style = ButtonStyle.Destructive }
                        .Alert(asking.Value, () => asking.Value = false, delete)
                        .Id("delete-save"),
                    new Button("Show a notice", () => informing.Value = true) { Style = ButtonStyle.Outline }
                        .Alert(informing.Value, () => informing.Value = false, new Dialog("Controller connected", "Player 2 can join from the title screen."))
                        .Id("notice"),
                },
                new Text($"The save was {said.Value}.").Foreground(Tone.Muted),
            };
        }
    }
}

/// <summary>A quit button with three ways out.</summary>
internal sealed record QuitDemo : UiView
{
    public override UiView Body
    {
        get
        {
            var asking = UseState(false);
            var said = UseState("still playing");
            var quit = new Dialog("Quit to the title screen?", "Your last save was 12 minutes ago.")
            {
                Buttons =
                [
                    new DialogButton("Save and quit", () => said.Value = "saved and quit"),
                    new DialogButton("Quit without saving", () => said.Value = "quit without saving", DialogRole.Destructive),
                    new DialogButton("Cancel", () => said.Value = "cancelled", DialogRole.Cancel),
                ],
            };
            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Button("Quit", () => asking.Value = true) { Style = ButtonStyle.Outline }
                    .ConfirmationDialog(asking.Value, () => asking.Value = false, quit)
                    .Id("quit"),
                new Text($"You {said.Value}.").Foreground(Tone.Muted),
            };
        }
    }
}

/// <summary>A sheet with a name to type and a switch, closed with Done.</summary>
internal sealed record SheetDemo : UiView
{
    public override UiView Body
    {
        get
        {
            var open = UseState(false);
            var name = UseState("Northwind");
            var hardcore = UseState(false);
            var form = new VStack(HorizontalAlignment.Leading, 16f)
            {
                new Text("New character").TextStyle(TextStyle.Title3),
                new TextField("Name", name.Value, value => name.Value = value).Id("character-name"),
                new Toggle("Hardcore", hardcore.Value, value => hardcore.Value = value).Id("hardcore"),
                new HStack(Spacing: 8f)
                {
                    new Spacer(),
                    new Button("Done", () => open.Value = false).DefaultFocus().Id("done"),
                },
            };
            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Button("New character", () => open.Value = true)
                    .Sheet(open.Value, () => open.Value = false, form)
                    .Id("new-character"),
                new Text($"{name.Value}{(hardcore.Value ? ", hardcore" : "")}.").Foreground(Tone.Muted),
            };
        }
    }
}

/// <summary>An info button with a few lines under it.</summary>
internal sealed record PopoverDemo : UiView
{
    public override UiView Body
    {
        get
        {
            var open = UseState(false);
            var tip = new VStack(HorizontalAlignment.Leading, 6f)
            {
                new Text("Stamina").TextStyle(TextStyle.Headline),
                new Text("Running and blocking use it.").Foreground(Tone.Muted),
                new Text("It fills again while you walk.").Foreground(Tone.Muted),
            };
            return new Button("Stamina", () => open.Value = !open.Value) { Style = ButtonStyle.Outline, Icon = IconName.Info }
                .Popover(open.Value, () => open.Value = false, tip)
                .Id("stamina");
        }
    }
}

/// <summary>Buttons that show toasts, and the toasts over the corner of the screen.</summary>
internal sealed record ToastDemo : UiView
{
    public override UiView Body
    {
        get
        {
            var toasts = UseState(new Toasts()).Value;
            var count = UseState(0);
            return new HStack(Spacing: 8f)
            {
                new Button("Save the game", () =>
                {
                    count.Value++;
                    toasts.Show(new Toast("Game saved", $"Slot 1 · save {count.Value}") { Icon = IconName.Check });
                }).Id("save-toast"),
                new Button("Quest updated", () => toasts.Show(new Toast("Quest updated", "The drowned bell: find the diver.") { Icon = IconName.Info }))
                {
                    Style = ButtonStyle.Outline,
                }.Id("quest-toast"),
            }.Toasts(toasts);
        }
    }
}
