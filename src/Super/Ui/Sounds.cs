namespace Super.Ui;

/// <summary>
/// One sound the interface plays: a cooked sound file of the game's, or one of the built-in sounds, which are made in
/// code so a game has them before it has any audio. It plays flat on <see cref="Channel.Interface"/>, so
/// <c>Audio.Volume(Channel.Interface, ...)</c> turns all of them up or down.
/// </summary>
public sealed record UiSound
{
    private UiSound(string name, Func<float[]>? make)
    {
        Name = name;
        Make = make;
    }

    /// <summary>The sound's name: the cooked file, such as <c>sounds/click.wav</c>, or <c>interface/press</c> and the like for a built-in one.</summary>
    public string Name { get; }

    /// <summary>How loud it plays, from 0 to 1.</summary>
    public float Volume { get; init; } = 1f;

    /// <summary>How far its pitch moves up or down each time, so a sound heard often doesn't tire. 0.04 is ±4%.</summary>
    public float PitchVariation { get; init; } = 0.04f;

    /// <summary>For a built-in sound, its samples, mono at <see cref="UiSynth.SampleRate"/>. Null for a file.</summary>
    internal Func<float[]>? Make { get; }

    /// <summary>A cooked sound file of the game's, by its name, such as <c>sounds/click.wav</c>.</summary>
    public static UiSound File(string name) => new(name, null);

    internal static UiSound BuiltIn(string name, Func<float[]> make) => new("interface/" + name, make);
}

/// <summary>
/// The sounds a theme's controls make, one for each thing that happens. Leave one null and that stays silent.
/// <see cref="Default"/> is a set of short soft ticks and blips made in code, and <see cref="None"/> is silent:
/// <code>Theme.Dark with { Sounds = UiSounds.None }</code>
/// </summary>
public sealed record UiSounds
{
    /// <summary>The pointer moving onto something that can be pressed.</summary>
    public UiSound? Hover { get; init; }

    /// <summary>The keyboard or the gamepad moving focus.</summary>
    public UiSound? Focus { get; init; }

    /// <summary>A button, row or anything else being pressed.</summary>
    public UiSound? Press { get; init; }

    /// <summary>Going back: closing a popup, or a step back in a navigation stack.</summary>
    public UiSound? Back { get; init; }

    /// <summary>A popup, sheet or dialog opening by itself. One that a press opens is heard as the press.</summary>
    public UiSound? Open { get; init; }

    /// <summary>A press on something disabled.</summary>
    public UiSound? Denied { get; init; }

    /// <summary>Left or right changing a slider, a stepper or a picker.</summary>
    public UiSound? Step { get; init; }

    /// <summary>Something held until it's done, such as a hold-to-confirm button.</summary>
    public UiSound? Confirm { get; init; }

    /// <summary>Short soft sounds made in code, the stock set.</summary>
    public static UiSounds Default { get; } = new()
    {
        Hover = UiSynth.Hover,
        Focus = UiSynth.Focus,
        Press = UiSynth.Press,
        Back = UiSynth.Back,
        Open = UiSynth.Open,
        Denied = UiSynth.Denied,
        Step = UiSynth.Step,
        Confirm = UiSynth.Confirm,
    };

    /// <summary>No sounds at all.</summary>
    public static UiSounds None { get; } = new();

    internal UiSound? this[UiCue cue] => cue switch
    {
        UiCue.Hover => Hover,
        UiCue.Focus => Focus,
        UiCue.Press => Press,
        UiCue.Back => Back,
        UiCue.Open => Open,
        UiCue.Denied => Denied,
        UiCue.Step => Step,
        _ => Confirm,
    };
}

/// <summary>What happened in the interface that may make a sound, one for each of <see cref="UiSounds"/>.</summary>
internal enum UiCue
{
    Hover,
    Focus,
    Press,
    Back,
    Open,
    Denied,
    Step,
    Confirm,
}

/// <summary>
/// The sounds the interface asked for since the frame last played them. Each sound is played once however often it was
/// asked for in between, and a popup opening just after a press stays quiet, since the press already said it.
/// </summary>
internal sealed class UiCues(UiClock clock)
{
    private const double OpenAfterPress = 0.25;

    private readonly List<UiSound> _pending = [];
    private double _pressedAt = double.NegativeInfinity;

    /// <summary>Asks for the sound this node's theme makes for this.</summary>
    public void Raise(UiNode node, UiCue cue) => Raise(node.Environment.Theme.Sounds, cue);

    public void Raise(UiSounds sounds, UiCue cue)
    {
        if (cue == UiCue.Press)
        {
            _pressedAt = clock.Now;
        }
        else if (cue == UiCue.Open && clock.Now - _pressedAt < OpenAfterPress)
        {
            return;
        }

        if (sounds[cue] is { } sound && !_pending.Contains(sound))
        {
            _pending.Add(sound);
        }
    }

    /// <summary>Hands over what's waiting, in the order it was asked for, and forgets it.</summary>
    public UiSound[] Take()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var taken = _pending.ToArray();
        _pending.Clear();
        return taken;
    }
}
