namespace Super.Ui;

/// <summary>How a prompt glyph is drawn: a keycap, a bare mouse, a round pad button, or a long pad button.</summary>
internal enum GlyphShape
{
    Keycap,
    Mouse,
    Disc,
    Pill,
}

/// <summary>
/// One key or button drawn in a prompt: its shape, the text or icon on it, the colour of that, and its name, which
/// a screen reader and a test read. <see cref="Device"/> is "Keyboard", "Mouse" or the pad's family.
/// </summary>
internal readonly record struct PromptGlyph(GlyphShape Shape, string Name, string Device, string? Text = null, IconName? Icon = null, Color? Tint = null);

/// <summary>Turns a binding into the glyphs that show it, for a keyboard and mouse or for each family of pad.</summary>
internal static class PromptGlyphs
{
    /// <summary>The glyphs for each source of a binding, in order: one for a key, four for a WASD composite.</summary>
    public static List<PromptGlyph> Of(Binding binding, GamepadFamily family)
    {
        var glyphs = new List<PromptGlyph>();
        switch (binding.Kind)
        {
            case BindingKind.MouseDelta:
                glyphs.Add(new PromptGlyph(GlyphShape.Mouse, "Mouse", "Mouse", Icon: IconName.Mouse));
                break;
            case BindingKind.MouseScroll:
                glyphs.Add(new PromptGlyph(GlyphShape.Mouse, "Wheel", "Mouse", Icon: IconName.MouseMiddle));
                break;
            default:
                foreach (var source in (ReadOnlySpan<Source>)[binding.First, binding.Second, binding.Third, binding.Fourth])
                {
                    if (source.Kind != SourceKind.None)
                    {
                        glyphs.Add(Of(source, family));
                    }
                }

                break;
        }

        return glyphs;
    }

    /// <summary>Whether a binding is read from a gamepad, as opposed to the keyboard and mouse.</summary>
    public static bool OnGamepad(Binding binding) =>
        binding.First.Kind is SourceKind.GamepadButton or SourceKind.GamepadTrigger or SourceKind.GamepadStick;

    private static PromptGlyph Of(Source source, GamepadFamily family) => source.Kind switch
    {
        SourceKind.Key => KeyGlyph((Key)source.Code),
        SourceKind.MouseButton => MouseGlyph((MouseButton)source.Code),
        SourceKind.GamepadButton => PadGlyphs.Button((GamepadButton)source.Code, family),
        SourceKind.GamepadTrigger => PadGlyphs.Trigger((GamepadTrigger)source.Code, family),
        _ => PadGlyphs.Stick((GamepadStick)source.Code, family),
    };

    private static PromptGlyph KeyGlyph(Key key) => key switch
    {
        Key.Up => Arrow("Up", IconName.ArrowUp),
        Key.Down => Arrow("Down", IconName.ArrowDown),
        Key.Left => Arrow("Left", IconName.ArrowLeft),
        Key.Right => Arrow("Right", IconName.ArrowRight),
        _ => new PromptGlyph(GlyphShape.Keycap, KeyName(key), "Keyboard", Text: KeyName(key)),
    };

    private static PromptGlyph Arrow(string name, IconName icon) => new(GlyphShape.Keycap, name, "Keyboard", Icon: icon);

    /// <summary>What a keycap says: the letter or digit, or a short word for the rest, as keyboards print them.</summary>
    internal static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= Key.KeyPad0 and <= Key.KeyPad9 => $"Num {(int)(key - Key.KeyPad0)}",
        Key.LeftSuper or Key.RightSuper => OperatingSystem.IsMacOS() ? "Cmd" : "Win",
        _ => ShortNames.GetValueOrDefault(key) ?? key.ToString(),
    };

    // The keys whose caps say something shorter than their name, or a character.
    private static readonly Dictionary<Key, string> ShortNames = new()
    {
        [Key.Escape] = "Esc",
        [Key.LeftShift] = "Shift",
        [Key.RightShift] = "Shift",
        [Key.LeftControl] = "Ctrl",
        [Key.RightControl] = "Ctrl",
        [Key.LeftAlt] = "Alt",
        [Key.RightAlt] = "Alt",
        [Key.Backspace] = "Back",
        [Key.Delete] = "Del",
        [Key.Insert] = "Ins",
        [Key.PageUp] = "PgUp",
        [Key.PageDown] = "PgDn",
        [Key.CapsLock] = "Caps",
        [Key.Apostrophe] = "'",
        [Key.Comma] = ",",
        [Key.Minus] = "-",
        [Key.Period] = ".",
        [Key.Slash] = "/",
        [Key.Semicolon] = ";",
        [Key.Equal] = "=",
        [Key.LeftBracket] = "[",
        [Key.Backslash] = "\\",
        [Key.RightBracket] = "]",
        [Key.GraveAccent] = "`",
    };

    private static PromptGlyph MouseGlyph(MouseButton button) => button switch
    {
        MouseButton.Left => new PromptGlyph(GlyphShape.Mouse, "Left click", "Mouse", Icon: IconName.MouseLeft),
        MouseButton.Right => new PromptGlyph(GlyphShape.Mouse, "Right click", "Mouse", Icon: IconName.MouseRight),
        MouseButton.Middle => new PromptGlyph(GlyphShape.Mouse, "Middle click", "Mouse", Icon: IconName.MouseMiddle),
        _ => new PromptGlyph(GlyphShape.Keycap, $"Mouse {(int)button + 1}", "Mouse", Text: $"M{(int)button + 1}"),
    };
}

/// <summary>What each family of pad calls its buttons and how it marks them.</summary>
internal static class PadGlyphs
{
    private static readonly Color Green = Color.Hex("#5DC14B");
    private static readonly Color Red = Color.Hex("#F0564B");
    private static readonly Color Blue = Color.Hex("#3D8FF0");
    private static readonly Color Yellow = Color.Hex("#F2C230");
    private static readonly Color PsBlue = Color.Hex("#8DB4F2");
    private static readonly Color PsRed = Color.Hex("#F27B7B");
    private static readonly Color PsPink = Color.Hex("#E59AD8");
    private static readonly Color PsGreen = Color.Hex("#4FD6A8");

    public static PromptGlyph Button(GamepadButton button, GamepadFamily family) => button switch
    {
        <= GamepadButton.North => Face(button, family),
        GamepadButton.LeftBumper => Pill(family, "LB", "L1", "L"),
        GamepadButton.RightBumper => Pill(family, "RB", "R1", "R"),
        GamepadButton.Back => Pill(family, "View", "Create", "−", "Back"),
        GamepadButton.Start => Pill(family, "Menu", "Options", "+", "Start"),
        GamepadButton.Guide => Pill(family, "Xbox", "PS", "Home", "Guide"),
        GamepadButton.LeftStick => Disc(family, "LS", "L3", "LS"),
        GamepadButton.RightStick => Disc(family, "RS", "R3", "RS"),
        _ => DPad(button, family),
    };

    /// <summary>Up, right, down or left on the d-pad, in <see cref="GamepadButton"/> order.</summary>
    private static PromptGlyph DPad(GamepadButton button, GamepadFamily family)
    {
        int at = button - GamepadButton.DPadUp;
        return new PromptGlyph(
            GlyphShape.Disc,
            ((string[])["D-pad up", "D-pad right", "D-pad down", "D-pad left"])[at],
            family.ToString(),
            Icon: ((IconName[])[IconName.DPadUp, IconName.DPadRight, IconName.DPadDown, IconName.DPadLeft])[at]);
    }

    public static PromptGlyph Trigger(GamepadTrigger trigger, GamepadFamily family) => trigger == GamepadTrigger.Left
        ? Pill(family, "LT", "L2", "ZL")
        : Pill(family, "RT", "R2", "ZR");

    public static PromptGlyph Stick(GamepadStick stick, GamepadFamily family) => stick == GamepadStick.Left
        ? Disc(family, "L", "L", "L", "Left stick")
        : Disc(family, "R", "R", "R", "Right stick");

    /// <summary>South, east, west and north: Xbox letters in their colours, PlayStation shapes, Nintendo's letters where they sit.</summary>
    private static PromptGlyph Face(GamepadButton button, GamepadFamily family)
    {
        int at = (int)button;
        string device = family.ToString();
        return family switch
        {
            GamepadFamily.PlayStation => new PromptGlyph(
                GlyphShape.Disc,
                ((string[])["Cross", "Circle", "Square", "Triangle"])[at],
                device,
                Icon: ((IconName[])[IconName.Close, IconName.Circle, IconName.Square, IconName.Triangle])[at],
                Tint: ((Color[])[PsBlue, PsRed, PsPink, PsGreen])[at]),
            GamepadFamily.Nintendo => Letter(((string[])["B", "A", "Y", "X"])[at], device, null),
            GamepadFamily.Xbox => Letter(((string[])["A", "B", "X", "Y"])[at], device, ((Color[])[Green, Red, Blue, Yellow])[at]),
            _ => Letter(((string[])["A", "B", "X", "Y"])[at], device, null),
        };
    }

    private static PromptGlyph Letter(string letter, string device, Color? tint) => new(GlyphShape.Disc, letter, device, Text: letter, Tint: tint);


    /// <summary>A long button's label in the family's words: Xbox, PlayStation, Nintendo, and the Xbox word for any other pad unless one is given.</summary>
    private static PromptGlyph Pill(GamepadFamily family, string xbox, string playStation, string nintendo, string? other = null)
    {
        string text = Pick(family, xbox, playStation, nintendo, other);
        return new PromptGlyph(GlyphShape.Pill, text, family.ToString(), Text: text);
    }

    private static PromptGlyph Disc(GamepadFamily family, string xbox, string playStation, string nintendo, string? name = null)
    {
        string text = Pick(family, xbox, playStation, nintendo, null);
        return new PromptGlyph(GlyphShape.Disc, name ?? text, family.ToString(), Text: text);
    }

    private static string Pick(GamepadFamily family, string xbox, string playStation, string nintendo, string? other) => family switch
    {
        GamepadFamily.PlayStation => playStation,
        GamepadFamily.Nintendo => nintendo,
        GamepadFamily.Xbox => xbox,
        _ => other ?? xbox,
    };
}
