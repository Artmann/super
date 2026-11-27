using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The stories for the keyboard and the gamepad: text fields, and moving focus between controls.</summary>
internal static class Typing
{
    private static readonly PickerOption<string>[] Windows =
    [
        new("Window", "window"),
        new("Borderless", "borderless"),
        new("Full screen", "full"),
    ];

    public static UiView TextFields() => Page(
        new Specimen(
            "Empty and filled",
            "The prompt shows in a muted colour until there's text. Click one, or Tab to it, and type.",
            new Live<(string Name, string Clan)>(
                ("", "Northwind"),
                (text, set) => new VStack(HorizontalAlignment.Leading, 12f)
                {
                    new TextField("Player name", text.Name, name => set(text with { Name = name })).Id("name"),
                    new TextField("Clan", text.Clan, clan => set(text with { Clan = clan })).Id("clan"),
                }.Frame(width: 320f))),
        new Specimen(
            "Secure",
            "A dot for each character, and it won't copy or cut. The game still gets the text.",
            new Live<string>(
                "",
                (password, set) => new VStack(HorizontalAlignment.Leading, 8f)
                {
                    new SecureField("Password", password, set).Id("password"),
                    new Text($"{password.Length} characters").TextStyle(TextStyle.Footnote).Foreground(Tone.Muted),
                }.Frame(width: 320f))),
        new Specimen(
            "Turned off",
            "Faded, and a click doesn't focus it.",
            new TextField("Server address", "eu-west.example.net", Disabled: true).Frame(width: 320f)),
        new Specimen(
            "A sign-in form",
            "Tab moves from field to field and Enter signs in from either, as OnSubmit says.",
            new SignIn()));

    public static UiView Focus() => Page(
        new Specimen(
            "Moving with the keys",
            "Tab and Shift+Tab go in order. The arrows, the d-pad and the left stick go to the nearest control that way. Enter, Space or South presses.",
            new LevelSelect()),
        new Specimen(
            "A game menu",
            "Menu rows with .FocusWraps(), so down from Quit comes round to Continue. The highlight slides from row to row. Theme.Focus = FocusLook.Ring gives a thin ring instead.",
            new Live<string>("nothing yet", GameMenu)),
        new Specimen(
            "Settings rows",
            "Up and down move between rows, and left and right change a slider, stepper or picker.",
            new Live<Settings>(new Settings(0.7f, 2, "borderless", true), SettingsRows)),
        new Specimen(
            "Any view",
            ".Focusable(...) makes a view of your own a stop, with what Enter does and what left and right do.",
            new Live<int>(0, Chest)));

    private static UiView GameMenu(string chosen, Action<string> set)
    {
        var menu = new VStack(Spacing: 2f);
        foreach (string row in new[] { "Continue", "New game", "Options", "Quit" })
        {
            menu.Add(new Button(row, () => set(row)) { Style = ButtonStyle.Menu }.Id($"menu-{row.ToLowerInvariant().Replace(' ', '-')}"));
        }

        return new VStack(HorizontalAlignment.Leading, 12f)
        {
            menu.FocusWraps().Frame(width: 260f),
            new Text($"Chose: {chosen}").Foreground(Tone.Muted),
        };
    }

    private static UiView SettingsRows(Settings settings, Action<Settings> set) => new VStack(HorizontalAlignment.Leading, 16f)
        {
            new HStack(Spacing: 16f)
            {
                new Text("Volume").Frame(width: 120f, alignment: Alignment.Leading),
                new Slider(settings.Volume, volume => set(settings with { Volume = volume })) { Step = 0.1f, Label = "Volume" }.Id("volume"),
            },
            new Stepper("Players", settings.Players, players => set(settings with { Players = players }), 1, 4).Id("players"),
            new HStack(Spacing: 16f)
            {
                new Text("Window").Frame(width: 120f, alignment: Alignment.Leading),
                new Picker<string>("Window", settings.Window, Windows, window => set(settings with { Window = window })).Id("window"),
            },
            new Toggle("Subtitles", settings.Subtitles, subtitles => set(settings with { Subtitles = subtitles })).Id("subtitles"),
        }.Frame(width: 420f);

    private static UiView Chest(int opened, Action<int> set) => Row(
        16f,
        new VStack(HorizontalAlignment.Leading, 4f)
        {
            new Label("Old chest", IconName.Plus).TextStyle(TextStyle.Headline),
            new Text(opened == 0 ? "Press Enter to open" : opened == 1 ? "Opened once" : $"Opened {opened} times").Foreground(Tone.Muted),
        }
        .Padding(16f)
        .Background(Color.Hex("#78716C") with { A = 0.18f })
        .CornerRadius(8f)
        .Focusable(() => set(opened + 1))
        .Semantics(UiRole.Button, "Old chest")
        .Id("chest"));
}

/// <summary>The settings rows' values.</summary>
internal sealed record Settings(float Volume, int Players, string Window, bool Subtitles);

/// <summary>Nine levels in a grid, with the first focused as the story opens and the fifth locked, and the last one pressed.</summary>
internal sealed record LevelSelect : UiView
{
    public override UiView Body
    {
        get
        {
            var pressed = UseState("none yet");
            var grid = new VStack(HorizontalAlignment.Leading, 8f);
            for (int row = 0; row < 3; row++)
            {
                var line = new HStack(Spacing: 8f);
                for (int column = 0; column < 3; column++)
                {
                    string level = $"Level {(row * 3) + column + 1}";
                    var button = new Button(level, () => pressed.Value = level) { Style = ButtonStyle.Outline }
                        .Frame(width: 120f)
                        .Id(level.ToLowerInvariant().Replace(' ', '-'));
                    line.Add(level == "Level 5" ? button.Disabled() : button);
                }

                grid.Add(line);
            }

            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                grid.DefaultFocus(),
                new Text($"Pressed: {pressed.Value}. Level 5 is locked, so focus passes it by.").Foreground(Tone.Muted),
            };
        }
    }
}

/// <summary>A name and a password, and what signing in said.</summary>
internal sealed record SignIn : UiView
{
    public override UiView Body
    {
        get
        {
            var name = UseState("");
            var password = UseState("");
            var said = UseState("");
            void Submit(string _) => said.Value = name.Value.Length == 0 || password.Value.Length == 0
                ? "Fill in both fields."
                : $"Signed in as {name.Value}.";
            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                new TextField("Account name", name.Value, text => name.Value = text) { OnSubmit = Submit }.Id("account"),
                new SecureField("Password", password.Value, text => password.Value = text) { OnSubmit = Submit }.Id("secret"),
                new HStack(Spacing: 12f)
                {
                    new Button("Sign in", () => Submit("")).Id("sign-in"),
                    new Text(said.Value).Foreground(Tone.Muted),
                },
            }.Frame(width: 320f);
        }
    }
}
