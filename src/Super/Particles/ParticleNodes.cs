using System.Numerics;
using System.Text;

namespace Super;

// The nodes an emitter is built from. Each works out its part of a particle from the
// particle's own dice and its age alone, so nothing is kept from frame to frame. The
// emitter runs them in stages: birth, shape, start speed, forces, motion, then looks.
// Within a stage they run in the order they were added.

/// <summary>Random numbers for one particle and one node. The same particle and node always roll the same.</summary>
internal readonly struct ParticleDice(uint particle, uint salt)
{
    /// <summary>A number from 0 up to 1. Each channel is independent of the others.</summary>
    public float Roll(uint channel) =>
        (ParticleHash.Mix(particle + (salt + channel) * 0x9E3779B9u) >> 8) * (1f / (1 << 24));

    /// <summary>A number from -1 up to 1.</summary>
    public float Signed(uint channel) => 2f * Roll(channel) - 1f;

    /// <summary>A direction with every way equally likely.</summary>
    public Vector3 Direction(uint channel)
    {
        float y = Signed(channel);
        float angle = Roll(channel + 1) * MathF.Tau;
        float across = MathF.Sqrt(MathF.Max(1f - y * y, 0f));
        return new Vector3(across * MathF.Cos(angle), y, across * MathF.Sin(angle));
    }
}

internal static class ParticleHash
{
    /// <summary>Mixes the bits of a number thoroughly, so neighbouring inputs give unrelated outputs.</summary>
    public static uint Mix(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }
    }
}

/// <summary>Where the effect is in the world, for nodes that move particles around it.</summary>
internal readonly record struct ParticleFrame(Vector3 Centre, Vector3 Up);

/// <summary>What the look nodes shape: everything about a particle but where it is.</summary>
internal struct ParticleLook
{
    public float Size;
    public Vector3 Color;
    public float Opacity;
    public float Rotation;
}

/// <summary>Any step of an emitter. Its name, with its settings, is what <see cref="ParticleEmitter.ToString"/> lists.</summary>
internal abstract record ParticleNode
{
    /// <summary>The name of the builder call without "Node", and the settings if it has any: <c>Over { Duration = 1 }</c>.</summary>
    public sealed override string ToString()
    {
        var settings = new StringBuilder();
        string name = GetType().Name;
        name = name.EndsWith("Node", StringComparison.Ordinal) ? name[..^4] : name;
        return PrintMembers(settings) ? $"{name} {{ {settings} }}" : name;
    }
}

// Birth: when each particle starts, in seconds from the emitter's start.

internal abstract record BirthNode : ParticleNode
{
    /// <summary>Seconds from the start to the last birth.</summary>
    public abstract float LastBirth();

    public abstract float Birth(int index, int count, float roll);
}

internal sealed record BurstNode : BirthNode
{
    public static readonly BurstNode Instance = new();

    public override float LastBirth() => 0f;

    public override float Birth(int index, int count, float roll) => 0f;
}

/// <summary>Births spread evenly, each in its own share of the time with a little jitter.</summary>
internal sealed record OverNode(float Duration) : BirthNode
{
    public override float LastBirth() => Duration;

    public override float Birth(int index, int count, float roll) => Duration * (index + roll) / count;
}

/// <summary>Births in a few bursts, the particles shared out between them in turn.</summary>
internal sealed record PulsesNode(int Times, float Every) : BirthNode
{
    public override float LastBirth() => (Times - 1) * Every;

    public override float Birth(int index, int count, float roll) => index % Times * Every;
}

// Shape: where in the emitter's space a particle starts, and which way is out from the middle.

internal abstract record ShapeNode : ParticleNode
{
    public abstract (Vector3 Start, Vector3 Outward) Start(in ParticleDice dice);

    protected static Vector3 Around(in ParticleDice dice)
    {
        float angle = dice.Roll(0) * MathF.Tau;
        return new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
    }
}

internal sealed record PointNode : ShapeNode
{
    public static readonly PointNode Instance = new();

    public override (Vector3 Start, Vector3 Outward) Start(in ParticleDice dice) => (Vector3.Zero, Around(dice));
}

/// <summary>Along the x axis. Out is mostly sideways off the line, fanning a little along it.</summary>
internal sealed record LineNode(float Length) : ShapeNode
{
    public override (Vector3 Start, Vector3 Outward) Start(in ParticleDice dice) =>
        (new Vector3((dice.Roll(1) - 0.5f) * Length, 0f, 0f), Vector3.Normalize(Around(dice) * new Vector3(0.35f, 0f, 1f)));
}

internal sealed record BoxNode(Vector3 Size) : ShapeNode
{
    public override (Vector3 Start, Vector3 Outward) Start(in ParticleDice dice)
    {
        var inBox = (new Vector3(dice.Roll(1), dice.Roll(2), dice.Roll(3)) - new Vector3(0.5f)) * Size;
        var flat = new Vector3(inBox.X, 0f, inBox.Z);
        return (inBox, flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Around(dice));
    }
}

/// <summary>A flat round patch, evenly covered.</summary>
internal sealed record DiscNode(float Radius) : ShapeNode
{
    public override (Vector3 Start, Vector3 Outward) Start(in ParticleDice dice)
    {
        var around = Around(dice);
        return (around * (Radius * MathF.Sqrt(dice.Roll(1))), around);
    }
}

/// <summary>Inside a ball, evenly filled. Out is straight away from the middle.</summary>
internal sealed record SphereNode(float Radius) : ShapeNode
{
    public override (Vector3 Start, Vector3 Outward) Start(in ParticleDice dice)
    {
        var direction = dice.Direction(1);
        return (direction * (Radius * MathF.Cbrt(dice.Roll(3))), direction);
    }
}

// Start speed: what each adds to a particle's speed when it's born, in the emitter's space.

internal abstract record SpeedNode : ParticleNode
{
    public abstract Vector3 Speed(in ParticleDice dice, Vector3 outward);
}

internal sealed record VelocityNode(Vector3 Velocity) : SpeedNode
{
    public override Vector3 Speed(in ParticleDice dice, Vector3 outward) => Velocity;
}

/// <summary>Out from the middle of the shape, at 50% to 150% of the speed.</summary>
internal sealed record OutwardNode(float Metres) : SpeedNode
{
    public override Vector3 Speed(in ParticleDice dice, Vector3 outward) => outward * (Metres * (0.5f + dice.Roll(0)));
}

/// <summary>Any direction, up to the speed.</summary>
internal sealed record ScatterNode(float Metres) : SpeedNode
{
    public override Vector3 Speed(in ParticleDice dice, Vector3 outward) => dice.Direction(0) * (Metres * dice.Roll(2));
}

/// <summary>Within a cone round a direction, evenly over its spread, at 75% to 125% of the speed.</summary>
internal sealed record ConeNode(Vector3 Direction, float Angle, float Metres) : SpeedNode
{
    public override Vector3 Speed(in ParticleDice dice, Vector3 outward)
    {
        // A direction in a cone round +y, then turned to point along the cone's own axis.
        float y = 1f - dice.Roll(0) * (1f - MathF.Cos(Angle));
        float around = dice.Roll(1) * MathF.Tau;
        float across = MathF.Sqrt(MathF.Max(1f - y * y, 0f));
        var local = new Vector3(across * MathF.Cos(around), y, across * MathF.Sin(around));
        var turn = Quaternion.Identity;
        float dot = Vector3.Dot(Vector3.UnitY, Direction);
        if (dot < -0.9999f)
        {
            turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        }
        else if (dot < 0.9999f)
        {
            var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Direction));
            turn = Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
        }

        return Vector3.Transform(local, turn) * (Metres * (0.75f + 0.5f * dice.Roll(2)));
    }
}

// Forces: the emitter adds these up, so the closed form of the motion stays one sum.

internal abstract record ForceNode : ParticleNode;

internal sealed record DragNode(float PerSecond) : ForceNode;

internal sealed record GravityNode(Vector3 Pull) : ForceNode;

/// <summary>Air moving at this speed. Drag carries particles along with it.</summary>
internal sealed record WindNode(Vector3 Velocity) : ForceNode;

// Motion: moves a particle after its flight is worked out, in the world.

internal abstract record MotionNode : ParticleNode
{
    public abstract Vector3 Move(in ParticleDice dice, float age, Vector3 position, in ParticleFrame frame);
}

/// <summary>
/// A smooth, wandering sway, like eddies in the air. Two waves on each axis with their own
/// phases, less the value at birth so a particle still starts where its shape put it.
/// </summary>
internal sealed record WobbleNode(float Metres, float PerSecond) : MotionNode
{
    public override Vector3 Move(in ParticleDice dice, float age, Vector3 position, in ParticleFrame frame)
    {
        float w = MathF.Tau * PerSecond;
        return position + new Vector3(Axis(dice, 0, w, age), Axis(dice, 2, w, age), Axis(dice, 4, w, age)) * Metres;
    }

    private static float Axis(in ParticleDice dice, uint channel, float w, float age)
    {
        float a = dice.Roll(channel) * MathF.Tau;
        float b = dice.Roll(channel + 1) * MathF.Tau;
        return 0.6f * (MathF.Sin(w * age + a) - MathF.Sin(a)) + 0.4f * (MathF.Sin(2.3f * w * age + b) - MathF.Sin(b));
    }
}

/// <summary>Turns particles round the effect's upright axis through its middle, like a dust devil.</summary>
internal sealed record SwirlNode(float TurnsPerSecond) : MotionNode
{
    public override Vector3 Move(in ParticleDice dice, float age, Vector3 position, in ParticleFrame frame)
    {
        var turn = Quaternion.CreateFromAxisAngle(frame.Up, TurnsPerSecond * MathF.Tau * age);
        return frame.Centre + Vector3.Transform(position - frame.Centre, turn);
    }
}

// Looks: each multiplies or adds to what the ones before it made.

internal abstract record LookNode : ParticleNode
{
    /// <param name="life">How far through its life the particle is, from 0 to 1.</param>
    public abstract void Apply(in ParticleDice dice, float age, float life, ref ParticleLook look);
}

/// <summary>Width from start to end, eased over the life, with each particle a little bigger or smaller.</summary>
internal sealed record SizeNode(float Start, float End, Easing Easing, float Spread) : LookNode
{
    public override void Apply(in ParticleDice dice, float age, float life, ref ParticleLook look) =>
        look.Size *= float.Lerp(Start, End, Ease.Apply(Easing, life)) * (1f + Spread * dice.Signed(0));
}

/// <summary>Color through stops at points in the life, blending evenly between them.</summary>
internal sealed record GradientNode(float[] At, Vector3[] Colors) : LookNode
{
    public override void Apply(in ParticleDice dice, float age, float life, ref ParticleLook look) => look.Color *= ColorAt(life);

    public Vector3 ColorAt(float life)
    {
        if (life <= At[0])
        {
            return Colors[0];
        }

        for (int i = 1; i < At.Length; i++)
        {
            if (life <= At[i])
            {
                float span = At[i] - At[i - 1];
                float amount = span > 0f ? (life - At[i - 1]) / span : 1f;
                return Vector3.Lerp(Colors[i - 1], Colors[i], amount);
            }
        }

        return Colors[^1];
    }

    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.AppendJoin(", ", At.Select((at, i) => $"{at}: {Colors[i]}"));
        return true;
    }
}

internal sealed record OpacityNode(float Opacity) : LookNode
{
    public override void Apply(in ParticleDice dice, float age, float life, ref ParticleLook look) => look.Opacity *= Opacity;
}

/// <summary>Fades in over the first part of the life and out over the last, smoothly.</summary>
internal sealed record FadeNode(float In, float Out) : LookNode
{
    public override void Apply(in ParticleDice dice, float age, float life, ref ParticleLook look)
    {
        float fadeIn = In > 0f ? SmoothStep(life / In) : 1f;
        float fadeOut = Out > 0f ? 1f - SmoothStep((life - (1f - Out)) / Out) : 1f;
        look.Opacity *= fadeIn * fadeOut;
    }

    private static float SmoothStep(float x)
    {
        float t = Math.Clamp(x, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>Opacity that dips and comes back, each particle at its own beat.</summary>
internal sealed record FlickerNode(float Amount, float PerSecond) : LookNode
{
    public override void Apply(in ParticleDice dice, float age, float life, ref ParticleLook look) =>
        look.Opacity *= 1f - Amount * (0.5f + 0.5f * MathF.Sin(MathF.Tau * PerSecond * age + dice.Roll(0) * MathF.Tau));
}

/// <summary>Turns each particle at its own speed, up to this fast either way.</summary>
internal sealed record SpinNode(float RadiansPerSecond) : LookNode
{
    public override void Apply(in ParticleDice dice, float age, float life, ref ParticleLook look) =>
        look.Rotation += RadiansPerSecond * dice.Signed(0) * age;
}
