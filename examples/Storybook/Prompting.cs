using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The story for button prompts: keys, the mouse, and each family of gamepad.</summary>
internal static class Prompting
{
    private static readonly Binding[] PadButtons =
    [
        GamepadButton.South, GamepadButton.East, GamepadButton.West, GamepadButton.North,
        GamepadButton.LeftBumper, GamepadTrigger.Right, GamepadButton.Start, GamepadButton.DPadUp, GamepadStick.Left,
    ];

    public static UiView Prompts() => Page(
        new Specimen(
            "Follows the player",
            "Says Space and E on the keyboard, and the pad's own buttons once a pad button or stick is used. ActionPrompt does this from your bindings, and follows a rebind.",
            new Hints()),
        new Specimen(
            "Each family of pad",
            "Xbox letters in their colours, PlayStation shapes, Nintendo's letters where they sit, and plain letters for a pad the name doesn't give away.",
            Families()),
        new Specimen(
            "Keys and the mouse",
            "Keycaps with the short names keyboards print, arrows as arrows, a composite as each of its keys, and the mouse with its button filled in.",
            Row(
                10f,
                new ButtonPrompt(Key.Space),
                new ButtonPrompt(Key.Escape),
                new ButtonPrompt(Key.LeftShift),
                new ButtonPrompt(Key.E),
                new ButtonPrompt(Keys.Arrows),
                new ButtonPrompt(Keys.WASD),
                new ButtonPrompt(MouseButton.Left),
                new ButtonPrompt(MouseButton.Right),
                new ButtonPrompt(Binding.MouseDelta))));

    private static UiView Families()
    {
        var rows = new VStack(HorizontalAlignment.Leading, 12f);
        foreach (var family in Enum.GetValues<GamepadFamily>())
        {
            var row = new HStack(Spacing: 8f) { new Text(family.ToString()).Foreground(Tone.Muted).Frame(width: 110f, alignment: Alignment.Leading) };
            foreach (var button in PadButtons)
            {
                row.Add(new ButtonPrompt(button) { Family = family });
            }

            rows.Add(row);
        }

        return rows;
    }
}

/// <summary>Two hints that switch between the keyboard and the pad plugged in, as the player does.</summary>
internal sealed record Hints : UiView
{
    public override UiView Body
    {
        get
        {
            bool pad = Environment.Device == InputDevice.Gamepad;
            return new HStack(Spacing: 24f)
            {
                Hint(pad ? GamepadButton.South : Key.Space, "Jump"),
                Hint(pad ? GamepadButton.West : Key.E, "Open"),
                new Text($"On: {Environment.Device}, {Environment.Gamepad} pad").Foreground(Tone.Muted),
            };
        }
    }

    private static UiView Hint(Binding binding, string label) => new HStack(Spacing: 8f) { new ButtonPrompt(binding), new Text(label) };
}
