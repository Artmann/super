namespace Super;

/// <summary>
/// How a move gets going and comes to rest. <c>In</c> eases at the start, <c>Out</c> at the
/// end, and <c>InOut</c> at both. The plain <see cref="In"/>, <see cref="Out"/> and
/// <see cref="InOut"/> are cubic. The rest are the usual Penner set, from gentle
/// (<c>Sine</c>) to strong (<c>Expo</c>), and three with character: <c>Back</c> goes a
/// little past and returns, <c>Elastic</c> wobbles like a spring, and <c>Bounce</c> drops
/// like a ball.
/// </summary>
public enum Easing
{
    /// <summary>The same speed all the way, starting and stopping at once.</summary>
    Linear,

    /// <summary>Starts slow and speeds up, ending at full speed. Cubic.</summary>
    In,

    /// <summary>Starts at full speed and slows to a stop. Cubic.</summary>
    Out,

    /// <summary>Starts slow and settles slowly, like a camera on a crane. Cubic.</summary>
    InOut,

    SineIn,
    SineOut,
    SineInOut,
    QuadIn,
    QuadOut,
    QuadInOut,
    QuartIn,
    QuartOut,
    QuartInOut,
    QuintIn,
    QuintOut,
    QuintInOut,
    ExpoIn,
    ExpoOut,
    ExpoInOut,
    CircIn,
    CircOut,
    CircInOut,

    /// <summary>Pulls back a little before it goes.</summary>
    BackIn,

    /// <summary>Goes a little past the end and settles back, like a building popping up.</summary>
    BackOut,

    BackInOut,

    /// <summary>Winds up like a spring before it lets go.</summary>
    ElasticIn,

    /// <summary>Springs past the end and wobbles to rest.</summary>
    ElasticOut,

    ElasticInOut,

    /// <summary>Bounces off the start before it goes.</summary>
    BounceIn,

    /// <summary>Lands and bounces, like a dropped ball.</summary>
    BounceOut,

    BounceInOut,
}

/// <summary>The sums behind <see cref="Easing"/>.</summary>
public static class Ease
{
    // The usual constants for these curves, from Robert Penner's set.
    private const float Back = 1.70158f;
    private const float BackInOutAmount = Back * 1.525f;
    private const float ElasticPeriod = 2f * MathF.PI / 3f;
    private const float ElasticInOutPeriod = 2f * MathF.PI / 4.5f;

    /// <summary>
    /// How far along a move is at this point of its time. The time is from 0 to 1, and
    /// anything outside is held at the ends. The answer starts at 0 and ends at 1, but
    /// <c>Back</c> and <c>Elastic</c> go past those on the way.
    /// </summary>
    public static float Apply(Easing easing, float amount)
    {
        float t = Math.Clamp(amount, 0f, 1f);
        switch (easing)
        {
            case Easing.Linear:
                return t;
            case Easing.BackInOut:
                return BackInOut(t);
            case Easing.ElasticInOut:
                return ElasticInOut(t);
            case Easing.BounceIn:
                return 1f - Bounce(1f - t);
            case Easing.BounceOut:
                return Bounce(t);
            case Easing.BounceInOut:
                return t < 0.5f ? (1f - Bounce(1f - 2f * t)) / 2f : (1f + Bounce(2f * t - 1f)) / 2f;
        }

        // The rest are an In curve, turned around for Out and joined to itself for InOut.
        var (family, side) = Split(easing);
        return side switch
        {
            Side.In => In(family, t),
            Side.Out => 1f - In(family, 1f - t),
            _ => t < 0.5f ? In(family, 2f * t) / 2f : 1f - In(family, 2f - 2f * t) / 2f,
        };
    }

    private enum Side
    {
        In,
        Out,
        InOut,
    }

    private static (Easing Family, Side Side) Split(Easing easing) => easing switch
    {
        Easing.In => (Easing.In, Side.In),
        Easing.Out => (Easing.In, Side.Out),
        Easing.InOut => (Easing.In, Side.InOut),
        _ => (Easing.SineIn + ((easing - Easing.SineIn) / 3) * 3, (Side)((easing - Easing.SineIn) % 3)),
    };

    /// <summary>The In curve of a family, named by its In member.</summary>
    private static float In(Easing family, float t) => family switch
    {
        Easing.In => t * t * t,
        Easing.SineIn => 1f - MathF.Cos(t * MathF.PI / 2f),
        Easing.QuadIn => t * t,
        Easing.QuartIn => t * t * t * t,
        Easing.QuintIn => t * t * t * t * t,
        Easing.ExpoIn => t <= 0f ? 0f : MathF.Pow(2f, 10f * t - 10f),
        Easing.CircIn => 1f - MathF.Sqrt(1f - t * t),
        Easing.BackIn => (Back + 1f) * t * t * t - Back * t * t,
        Easing.ElasticIn => t <= 0f ? 0f : t >= 1f ? 1f : -MathF.Pow(2f, 10f * t - 10f) * MathF.Sin((10f * t - 10.75f) * ElasticPeriod),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "There is no such easing."),
    };

    private static float BackInOut(float t)
    {
        const float c = BackInOutAmount;
        if (t < 0.5f)
        {
            float u = 2f * t;
            return u * u * ((c + 1f) * u - c) / 2f;
        }

        float v = 2f * t - 2f;
        return (v * v * ((c + 1f) * v + c) + 2f) / 2f;
    }

    private static float ElasticInOut(float t)
    {
        if (t <= 0f || t >= 1f)
        {
            return t;
        }

        float wave = MathF.Sin((20f * t - 11.125f) * ElasticInOutPeriod);
        return t < 0.5f
            ? -(MathF.Pow(2f, 20f * t - 10f) * wave) / 2f
            : MathF.Pow(2f, -20f * t + 10f) * wave / 2f + 1f;
    }

    /// <summary>A ball dropped from 1 bouncing to rest at 0, read upside down: Bounce Out.</summary>
    private static float Bounce(float t)
    {
        const float strength = 7.5625f;
        const float width = 2.75f;
        if (t < 1f / width)
        {
            return strength * t * t;
        }

        if (t < 2f / width)
        {
            t -= 1.5f / width;
            return strength * t * t + 0.75f;
        }

        if (t < 2.5f / width)
        {
            t -= 2.25f / width;
            return strength * t * t + 0.9375f;
        }

        t -= 2.625f / width;
        return strength * t * t + 0.984375f;
    }
}
