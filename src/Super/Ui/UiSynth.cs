namespace Super.Ui;

/// <summary>
/// The built-in interface sounds, made in code: short tones that glide in pitch and die away, a touch of noise on a
/// press, a low buzz for no, and a two-note chime for done. Each is made once, the first time it plays.
/// </summary>
internal static class UiSynth
{
    public const int SampleRate = 44100;

    public static UiSound Hover { get; } = UiSound.BuiltIn("hover", () => Tone(new Glide(2300f, 2100f, Seconds: 0.03f, Decay: 160f), 0.10f));

    public static UiSound Focus { get; } = UiSound.BuiltIn("focus", () => Tone(new Glide(1500f, 1750f, Seconds: 0.05f, Decay: 85f), 0.20f, overtone: 0.2f));

    public static UiSound Press { get; } = UiSound.BuiltIn("press", () => WithNoise(Tone(new Glide(880f, 560f, Seconds: 0.09f, Decay: 48f), 0.32f, overtone: 0.3f), 0.004f, 0.12f));

    public static UiSound Back { get; } = UiSound.BuiltIn("back", () => Tone(new Glide(720f, 430f, Seconds: 0.1f, Decay: 36f), 0.26f, overtone: 0.15f));

    public static UiSound Open { get; } = UiSound.BuiltIn("open", () => Tone(new Glide(520f, 880f, Seconds: 0.14f, Decay: 24f, Attack: 0.012f), 0.22f, overtone: 0.2f));

    public static UiSound Denied { get; } = UiSound.BuiltIn("denied", MakeDenied);

    public static UiSound Step { get; } = UiSound.BuiltIn("step", () => Tone(new Glide(1100f, 1050f, Seconds: 0.035f, Decay: 110f), 0.20f));

    public static UiSound Confirm { get; } = UiSound.BuiltIn("confirm", MakeConfirm);

    /// <summary>A tone gliding from one pitch to another, rising over <see cref="Attack"/> seconds and then dying away at <see cref="Decay"/> a second.</summary>
    private readonly record struct Glide(float From, float To, float Seconds, float Decay, float Attack = 0.002f);

    /// <summary>One gliding tone, at this peak, with a little of the fifth above it for colour.</summary>
    private static float[] Tone(Glide glide, float peak, float overtone = 0f)
    {
        var samples = new float[(int)(glide.Seconds * SampleRate)];
        Add(samples, glide, peak, overtone, start: 0);
        return samples;
    }

    private static void Add(float[] samples, Glide glide, float peak, float overtone, int start)
    {
        int length = Math.Min((int)(glide.Seconds * SampleRate), samples.Length - start);
        double phase = 0.0;
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / SampleRate;
            float along = t / glide.Seconds;
            float pitch = glide.From + (glide.To - glide.From) * along;
            phase += 2.0 * Math.PI * pitch / SampleRate;
            float wave = MathF.Sin((float)phase) + overtone * MathF.Sin((float)(phase * 1.5));
            samples[start + i] += wave * peak * Envelope(t, glide, along) / (1f + overtone);
        }
    }

    /// <summary>Up over the attack, down exponentially after it, and to nothing over the last tenth so the end doesn't click.</summary>
    private static float Envelope(float t, Glide glide, float along)
    {
        float shape = t < glide.Attack ? t / glide.Attack : MathF.Exp(-(t - glide.Attack) * glide.Decay);
        return along > 0.9f ? shape * (1f - along) * 10f : shape;
    }

    /// <summary>A short burst of noise over the start, the snap of a press.</summary>
    private static float[] WithNoise(float[] samples, float seconds, float peak)
    {
        // A fixed seed, so the sound is the same every time and in every test.
        var random = new Random(7);
        int length = Math.Min((int)(seconds * SampleRate), samples.Length);
        for (int i = 0; i < length; i++)
        {
            // In over half a millisecond, so the snap doesn't click, and out by the end.
            float fade = MathF.Min(1f, i / (0.0005f * SampleRate)) * (1f - (float)i / length);
            samples[i] += ((float)random.NextDouble() * 2f - 1f) * peak * fade;
        }

        return samples;
    }

    /// <summary>Two low buzzes close together, as a game says no.</summary>
    private static float[] MakeDenied()
    {
        var samples = new float[(int)(0.17f * SampleRate)];
        var buzz = new Glide(170f, 160f, Seconds: 0.065f, Decay: 20f, Attack: 0.004f);
        Add(samples, buzz, 0.2f, overtone: 0.6f, start: 0);
        Add(samples, buzz, 0.2f, overtone: 0.6f, start: (int)(0.095f * SampleRate));
        return samples;
    }

    /// <summary>A rising pair of notes, a fifth apart, for something finished.</summary>
    private static float[] MakeConfirm()
    {
        var samples = new float[(int)(0.32f * SampleRate)];
        Add(samples, new Glide(660f, 660f, Seconds: 0.14f, Decay: 22f, Attack: 0.004f), 0.22f, overtone: 0.15f, start: 0);
        Add(samples, new Glide(990f, 990f, Seconds: 0.24f, Decay: 13f, Attack: 0.004f), 0.24f, overtone: 0.15f, start: (int)(0.08f * SampleRate));
        return samples;
    }
}
