using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The story for controls made for games: arrow pickers, hold buttons, key binders and a radial menu.</summary>
internal static class GameControls
{
    public static UiView Story() => Page(
        new Specimen(
            "Arrow pickers",
            "One choice at a time between two arrows, as console settings screens do. Left and right step it, and it goes round at the ends.",
            new Arrows().Frame(width: 360f)),
        new Specimen(
            "Hold to confirm",
            "Fills while the mouse, Enter or South is held, and acts once full. Let go early and it empties.",
            new Holding()),
        new Specimen(
            "Key binders",
            "Click, Enter or South, then press the new key. Escape stops listening. Reset shows once it's changed, and a clash shows beside it.",
            new Binders().Frame(width: 480f)),
        new Specimen(
            "Radial menu",
            "Aim with the mouse or the left stick, step round with left and right, and pick with a click, Enter or South.",
            new Wheel()));
}

internal sealed record Arrows : UiView
{
    private static readonly PickerOption<string>[] Qualities = [new("Low", "Low"), new("Medium", "Medium"), new("High", "High"), new("Ultra", "Ultra")];
    private static readonly PickerOption<string>[] Modes = [new("Windowed", "Windowed"), new("Borderless", "Borderless"), new("Fullscreen", "Fullscreen")];

    public override UiView Body
    {
        get
        {
            var quality = UseState("High");
            var mode = UseState("Borderless");
            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                Row("Shadows", new Picker<string>("Shadows", quality.Value, Qualities, value => quality.Value = value) { Style = PickerStyle.Arrows }),
                Row("Window", new Picker<string>("Window", mode.Value, Modes, value => mode.Value = value) { Style = PickerStyle.Arrows }),
            };
        }
    }

    private static UiView Row(string name, UiView picker) =>
        new HStack(VerticalAlignment.Center, 16f) { new Text(name).Frame(width: 90f, alignment: Alignment.Leading), picker };
}

internal sealed record Holding : UiView
{
    public override UiView Body
    {
        get
        {
            var deleted = UseState(0);
            return new HStack(VerticalAlignment.Center, 16f)
            {
                new HoldButton("Hold to delete save", () => deleted.Value++),
                new HoldButton("Hold to skip", Seconds: 0.5f),
                new HoldButton("Turned off") { Disabled = true },
                new Text(deleted.Value == 0 ? "Nothing deleted yet" : $"Deleted {deleted.Value} times").Foreground(Tone.Muted),
            };
        }
    }
}

internal sealed record Binders : UiView
{
    public override UiView Body
    {
        get
        {
            var jump = UseState<Binding>(Key.Space);
            var interact = UseState<Binding>(Key.E);
            var padJump = UseState<Binding>(GamepadButton.South);
            string? clash = jump.Value == interact.Value ? "Interact" : null;
            return new VStack(HorizontalAlignment.Leading, 6f)
            {
                new KeyBinder("Jump", jump.Value, value => jump.Value = value) { Default = Key.Space, Clash = clash },
                new KeyBinder("Interact", interact.Value, value => interact.Value = value) { Default = Key.E, Clash = clash is null ? null : "Jump" },
                new KeyBinder("Jump on the pad", padJump.Value, value => padJump.Value = value) { Device = InputDevice.Gamepad, Default = GamepadButton.South },
            };
        }
    }
}

internal sealed record Wheel : UiView
{
    private static readonly (string Name, IconName Icon)[] Choices =
    [
        ("Sword", IconName.Triangle), ("Bow", IconName.ArrowRight), ("Potion", IconName.Circle), ("Map", IconName.Search),
        ("Torch", IconName.Dot), ("Shield", IconName.Square),
    ];

    public override UiView Body
    {
        get
        {
            var picked = UseState("Nothing");
            var items = Choices.Select(choice => new RadialItem(choice.Name, () => picked.Value = choice.Name) { Icon = choice.Icon }).ToList();
            return new HStack(VerticalAlignment.Center, 24f)
            {
                new RadialMenu("Quick items", items) { TakeFocus = false },
                new Text($"In hand: {picked.Value}").Foreground(Tone.Muted),
            };
        }
    }
}
