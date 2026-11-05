using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// One kind of particle, built from nodes the way a particle system is in Unity or
/// Unreal: where they're born, how they start moving, the forces on them and how they
/// look over their lives. Each call adds a node and gives back a new emitter, so a shared
/// one is never changed and a variation is one more call:
/// <code>
/// static readonly ParticleEmitter Dust = new ParticleEmitter(count: 40, lifetime: 1.6f)
///     .FromLine()
///     .Outward(2.2f)
///     .Drag(2.5f)
///     .Gravity(new Vector3(0f, 0.25f, 0f))
///     .Size(0.5f, 1.8f)
///     .Color(tan, grey)
///     .Fade(0.1f, 0.6f);
/// </code>
/// Nothing is simulated from frame to frame: each particle is worked out from the key,
/// its number and the age, so a paused, loaded or recorded game shows the same puff.
/// Draw one with <see cref="View.Particles(int, ParticleEffect, Vector3, float)"/>, or put
/// several on a timeline in a <see cref="ParticleEffect"/>.
/// </summary>
/// <remarks>
/// The timing, the shape, the count, the lifetime and the seed have one setting each, and
/// setting one again replaces it. Every other node stacks: speeds and forces add up,
/// motions apply in turn, and sizes, colors and opacities multiply, starting from one
/// metre wide, white and solid. Spawn shapes and speeds are in the emitter's own space
/// and follow the transform it's drawn with; gravity, wind, sizes and colors are in the
/// world.
/// </remarks>
public sealed class ParticleEmitter
{
    private readonly ParticleNode[] _nodes;
    private ParticleEffect? _alone;

    // The nodes sorted into their stages, each with the salt that keeps its dice its own.
    private readonly (SpeedNode Node, uint Salt)[] _speeds;
    private readonly (MotionNode Node, uint Salt)[] _motions;
    private readonly (LookNode Node, uint Salt)[] _looks;
    private readonly float _drag;
    private readonly Vector3 _pull;

    private const uint ShapeSalt = 64;

    /// <summary>An emitter that bursts this many particles from a point, each lasting about this many seconds.</summary>
    /// <param name="lifetimeSpread">How much lifetimes differ, as a fraction: 0.3 gives 70% to 130%. 0 to 0.9.</param>
    public ParticleEmitter(int count, float lifetime, float lifetimeSpread = 0.3f)
        : this(CheckCount(count), CheckLifetime(lifetime), Fraction(lifetimeSpread, nameof(lifetimeSpread), 0.9f), 0, BurstNode.Instance, PointNode.Instance, [])
    {
    }

    private ParticleEmitter(int count, float lifetime, float lifetimeSpread, int seed, BirthNode birth, ShapeNode shape, ParticleNode[] nodes)
    {
        Count = count;
        Lifetime = lifetime;
        LifetimeSpread = lifetimeSpread;
        SeedValue = seed;
        Birth = birth;
        Shape = shape;
        _nodes = nodes;

        var speeds = new List<(SpeedNode, uint)>();
        var motions = new List<(MotionNode, uint)>();
        var looks = new List<(LookNode, uint)>();
        var wind = Vector3.Zero;
        for (int i = 0; i < nodes.Length; i++)
        {
            uint salt = (uint)(i + 2) * 64;
            switch (nodes[i])
            {
                case SpeedNode speed:
                    speeds.Add((speed, salt));
                    break;
                case MotionNode motion:
                    motions.Add((motion, salt));
                    break;
                case LookNode look:
                    looks.Add((look, salt));
                    break;
                case DragNode drag:
                    _drag += drag.PerSecond;
                    break;
                case GravityNode gravity:
                    _pull += gravity.Pull;
                    break;
                case WindNode w:
                    wind += w.Velocity;
                    break;
            }
        }

        // Drag pulls a particle's speed towards the air's, which is the same as a steady
        // push of drag times the wind's speed.
        _pull += wind * _drag;
        _speeds = [.. speeds];
        _motions = [.. motions];
        _looks = [.. looks];
    }

    /// <summary>How many particles there are, from 1 to <see cref="Particle.MaxPerFrame"/>.</summary>
    public int Count { get; }

    /// <summary>How long each particle lasts on average, in seconds.</summary>
    public float Lifetime { get; }

    public float LifetimeSpread { get; }

    /// <summary>Seconds from the start until the last particle has gone. Past this, drawing adds nothing.</summary>
    public float Duration => Birth.LastBirth() + Lifetime * (1f + LifetimeSpread);

    private int SeedValue { get; }

    private BirthNode Birth { get; }

    private ShapeNode Shape { get; }

    /// <summary>An effect of this emitter alone, made once, so drawing an emitter allocates nothing.</summary>
    public static implicit operator ParticleEffect(ParticleEmitter emitter) =>
        emitter._alone ??= new ParticleEffect().Add(emitter);

    // Settings with one value each.

    /// <summary>Changes how many particles there are.</summary>
    public ParticleEmitter WithCount(int count) => new(CheckCount(count), Lifetime, LifetimeSpread, SeedValue, Birth, Shape, _nodes);

    /// <summary>Changes how long particles last, and how much that differs, as a fraction from 0 to 0.9.</summary>
    public ParticleEmitter WithLifetime(float lifetime, float spread = 0.3f) =>
        new(Count, CheckLifetime(lifetime), Fraction(spread, nameof(spread), 0.9f), SeedValue, Birth, Shape, _nodes);

    /// <summary>Changes the pattern for every key, so two emitters that are otherwise alike don't match.</summary>
    public ParticleEmitter Seed(int seed) => new(Count, Lifetime, LifetimeSpread, seed, Birth, Shape, _nodes);

    // When particles are born.

    /// <summary>All the particles are born at once. This is how an emitter starts.</summary>
    public ParticleEmitter Burst() => WithBirth(BurstNode.Instance);

    /// <summary>Spreads births evenly over this many seconds.</summary>
    public ParticleEmitter Over(float seconds) => WithBirth(new OverNode(Positive(seconds, nameof(seconds))));

    /// <summary>Shares the particles between this many bursts, this many seconds apart, like puffs from a chimney.</summary>
    public ParticleEmitter Pulses(int times, float every)
    {
        if (times < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(times), times, "There must be at least one pulse.");
        }

        return WithBirth(new PulsesNode(times, Positive(every, nameof(every))));
    }

    // Where particles are born, in the emitter's own space.

    /// <summary>Every particle starts at the middle. This is how an emitter starts.</summary>
    public ParticleEmitter FromPoint() => WithShape(PointNode.Instance);

    /// <summary>
    /// Along a line on the x axis through the middle, this many metres long. Drawn with
    /// <see cref="View.Particles(int, ParticleEffect, Vector3, Vector3, float)"/>, a line one
    /// metre long runs from one end to the other. Outward speed throws particles mostly
    /// sideways, off the line.
    /// </summary>
    public ParticleEmitter FromLine(float length = 1f) => WithShape(new LineNode(NotNegative(length, nameof(length))));

    /// <summary>Anywhere in a box this many metres across on each axis, centred on the middle.</summary>
    public ParticleEmitter FromBox(Vector3 size)
    {
        if (!(size.X >= 0f && size.Y >= 0f && size.Z >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A spawn box can't be smaller than nothing.");
        }

        return WithShape(new BoxNode(size));
    }

    /// <summary>Anywhere on a flat round patch of this radius, such as round the foot of a tree.</summary>
    public ParticleEmitter FromDisc(float radius) => WithShape(new DiscNode(NotNegative(radius, nameof(radius))));

    /// <summary>Anywhere inside a ball of this radius. Outward speed throws particles straight away from the middle.</summary>
    public ParticleEmitter FromSphere(float radius) => WithShape(new SphereNode(NotNegative(radius, nameof(radius))));

    // How particles start moving. These add up.

    /// <summary>A speed every particle starts with, in metres per second.</summary>
    public ParticleEmitter Velocity(Vector3 velocity) => Then(new VelocityNode(Finite(velocity, nameof(velocity))));

    /// <summary>Flings particles out from the middle of the shape at about this many metres per second, from half to one and a half times it.</summary>
    public ParticleEmitter Outward(float speed) => Then(new OutwardNode(NotNegative(speed, nameof(speed))));

    /// <summary>Adds up to this many metres per second in a random direction.</summary>
    public ParticleEmitter Scatter(float speed) => Then(new ScatterNode(NotNegative(speed, nameof(speed))));

    /// <summary>Shoots particles within a cone round a direction, this many radians either side of it, at about this speed.</summary>
    public ParticleEmitter Cone(Vector3 direction, float angle, float speed)
    {
        if (!(direction.LengthSquared() > 1e-8f) || !float.IsFinite(direction.LengthSquared()))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "A cone needs a direction to point in.");
        }

        if (!(angle >= 0f && angle <= MathF.PI))
        {
            throw new ArgumentOutOfRangeException(nameof(angle), angle, "A cone's angle must be from 0 to pi radians.");
        }

        return Then(new ConeNode(Vector3.Normalize(direction), angle, NotNegative(speed, nameof(speed))));
    }

    // Forces. These add up.

    /// <summary>
    /// Slows particles down, per second. At 2, a particle has lost about 86% of its speed
    /// after one second. It also caps how fast gravity can move them.
    /// </summary>
    public ParticleEmitter Drag(float perSecond) => Then(new DragNode(NotNegative(perSecond, nameof(perSecond))));

    /// <summary>A pull in metres per second, per second, in the world. A positive y rises, as warm air does.</summary>
    public ParticleEmitter Gravity(Vector3 pull) => Then(new GravityNode(Finite(pull, nameof(pull))));

    /// <summary>Air moving this fast in the world. It needs <see cref="Drag"/> to catch particles; the more drag, the sooner they move with it.</summary>
    public ParticleEmitter Wind(Vector3 velocity) => Then(new WindNode(Finite(velocity, nameof(velocity))));

    // Motion on top of the flight. These apply in the order they were added.

    /// <summary>A smooth, wandering sway of up to about this many metres, changing about this many times a second.</summary>
    public ParticleEmitter Wobble(float metres, float perSecond = 1f) =>
        Then(new WobbleNode(NotNegative(metres, nameof(metres)), NotNegative(perSecond, nameof(perSecond))));

    /// <summary>Turns particles round the effect's upright axis through its middle, this many times a second. Negative turns the other way.</summary>
    public ParticleEmitter Swirl(float turnsPerSecond) => Then(new SwirlNode(FiniteValue(turnsPerSecond, nameof(turnsPerSecond))));

    // How particles look over their lives. These multiply.

    /// <summary>
    /// Width in metres from birth to death, eased over the life. Out grows fast at first and
    /// then slows, as a billow does. Spread makes each a little bigger or smaller, as a fraction.
    /// </summary>
    public ParticleEmitter Size(float start, float end, Easing easing = Easing.Out, float spread = 0.3f) =>
        Then(new SizeNode(NotNegative(start, nameof(start)), NotNegative(end, nameof(end)), easing, Fraction(spread, nameof(spread), 0.9f)));

    /// <summary>One color all its life, as linear red, green and blue from 0 to 1 like a model's color. The sun, the sky and point and spot lights light it.</summary>
    public ParticleEmitter Color(Vector3 color) => Color(color, color);

    /// <summary>A color blending evenly from birth to death.</summary>
    public ParticleEmitter Color(Vector3 start, Vector3 end) => Gradient((0f, start), (1f, end));

    /// <summary>Colors at points through the life, from 0 at birth to 1 at death, blending between them. The points must go up.</summary>
    public ParticleEmitter Gradient(params ReadOnlySpan<(float At, Vector3 Color)> stops)
    {
        if (stops.IsEmpty)
        {
            throw new ArgumentException("A gradient needs at least one color.", nameof(stops));
        }

        var at = new float[stops.Length];
        var colors = new Vector3[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            if (!(stops[i].At >= 0f && stops[i].At <= 1f) || (i > 0 && stops[i].At < stops[i - 1].At))
            {
                throw new ArgumentOutOfRangeException(nameof(stops), stops[i].At, "A gradient's points must go up from 0 to 1.");
            }

            at[i] = stops[i].At;
            colors[i] = NotNegative(stops[i].Color, nameof(stops));
        }

        return Then(new GradientNode(at, colors));
    }

    /// <summary>How solid the middle of a particle is at most, from 0 to 1. Overlapping particles add up.</summary>
    public ParticleEmitter Opacity(float opacity) => Then(new OpacityNode(Fraction(opacity, nameof(opacity), 1f)));

    /// <summary>
    /// Makes each particle shine with its own colour times this, on top of the light that falls
    /// on it, as a flame or a spark does. Past 1 it blooms. Without it particles only show the
    /// light that reaches them.
    /// </summary>
    public ParticleEmitter Glow(float strength) => Then(new GlowNode(NotNegative(strength, nameof(strength))));

    /// <summary>Fades in over this part of the life and out over this last part, both from 0 to 1.</summary>
    public ParticleEmitter Fade(float fadeIn, float fadeOut) =>
        Then(new FadeNode(Fraction(fadeIn, nameof(fadeIn), 1f), Fraction(fadeOut, nameof(fadeOut), 1f)));

    /// <summary>Dips the opacity by up to this fraction, each particle at its own beat, about this many times a second.</summary>
    public ParticleEmitter Flicker(float amount, float perSecond) =>
        Then(new FlickerNode(Fraction(amount, nameof(amount), 1f), NotNegative(perSecond, nameof(perSecond))));

    /// <summary>Turns each particle at its own speed, up to this many radians a second either way.</summary>
    public ParticleEmitter Spin(float radiansPerSecond) => Then(new SpinNode(NotNegative(radiansPerSecond, nameof(radiansPerSecond))));

    /// <summary>Lists the emitter's nodes in order, which helps when a puff doesn't look as expected.</summary>
    public override string ToString() =>
        $"ParticleEmitter {Count} x {Lifetime} s: {string.Join(" -> ", new ParticleNode[] { Birth, Shape }.Concat(_nodes))}";

    /// <summary>
    /// Adds the particles alive at this age. The seed comes from the key and the place in
    /// the effect; the offset and the shape's x are stretched for a line drawn from and to.
    /// </summary>
    internal void Emit(uint seed, float age, in Matrix4x4 transform, Vector3 offset, float stretch, List<Particle> into)
    {
        if (!(age >= 0f) || age >= Duration)
        {
            return;
        }

        uint emitterSeed = ParticleHash.Mix(seed ^ ParticleHash.Mix(unchecked((uint)SeedValue + 0x632BE5ABu)));
        var frame = new ParticleFrame(
            Vector3.Transform(new Vector3(offset.X * stretch, offset.Y, offset.Z), transform),
            SafeNormalize(Vector3.TransformNormal(Vector3.UnitY, transform)));
        for (int i = 0; i < Count; i++)
        {
            uint particle = ParticleHash.Mix(emitterSeed + unchecked((uint)i * 0x85EBCA6Bu));
            var core = new ParticleDice(particle, 0);
            float birth = Birth.Birth(i, Count, core.Roll(0));
            float life = Lifetime * (1f + LifetimeSpread * core.Signed(1));
            float t = age - birth;
            if (t < 0f || t >= life)
            {
                continue;
            }

            var (start, outward) = Shape.Start(new ParticleDice(particle, ShapeSalt));
            var local = start + offset;
            local.X = (start.X + offset.X) * stretch;

            var velocity = Vector3.Zero;
            foreach (var (node, salt) in _speeds)
            {
                velocity += node.Speed(new ParticleDice(particle, salt), outward);
            }

            // Closed form of drag slowing the start speed while a steady force pulls:
            // travel is v (1 - e^-kt) / k, and the pull's share is g (t - that) / k.
            float travelled;
            float pulled;
            if (_drag > 0f)
            {
                travelled = (1f - MathF.Exp(-_drag * t)) / _drag;
                pulled = (t - travelled) / _drag;
            }
            else
            {
                travelled = t;
                pulled = 0.5f * t * t;
            }

            var position = Vector3.Transform(local, transform)
                + Vector3.TransformNormal(velocity, transform) * travelled
                + _pull * pulled;
            foreach (var (node, salt) in _motions)
            {
                position = node.Move(new ParticleDice(particle, salt), t, position, frame);
            }

            float f = t / life;
            var look = new ParticleLook { Size = 1f, Color = Vector3.One, Opacity = 1f, Rotation = core.Roll(2) * MathF.Tau };
            foreach (var (node, salt) in _looks)
            {
                node.Apply(new ParticleDice(particle, salt), t, f, ref look);
            }

            into.Add(new Particle
            {
                Position = position,
                Size = look.Size,
                Color = new Vector4(look.Color, look.Opacity),
                Rotation = look.Rotation,
                Variant = core.Roll(3) * 64f,
                Glow = look.Glow,
            });
        }
    }

    private ParticleEmitter WithBirth(BirthNode birth) => new(Count, Lifetime, LifetimeSpread, SeedValue, birth, Shape, _nodes);

    private ParticleEmitter WithShape(ShapeNode shape) => new(Count, Lifetime, LifetimeSpread, SeedValue, Birth, shape, _nodes);

    private ParticleEmitter Then(ParticleNode node) => new(Count, Lifetime, LifetimeSpread, SeedValue, Birth, Shape, [.. _nodes, node]);

    private static Vector3 SafeNormalize(Vector3 value) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.UnitY;

    private static int CheckCount(int count) =>
        count is >= 1 and <= Particle.MaxPerFrame
            ? count
            : throw new ArgumentOutOfRangeException(nameof(count), count, $"An emitter needs 1 to {Particle.MaxPerFrame} particles.");

    private static float CheckLifetime(float lifetime) =>
        lifetime > 0f && float.IsFinite(lifetime)
            ? lifetime
            : throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A particle's lifetime must be more than zero seconds.");

    private static float Positive(float value, string name) =>
        value > 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} must be more than zero.");

    private static float NotNegative(float value, string name) =>
        value >= 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} can't be negative.");

    private static Vector3 NotNegative(Vector3 value, string name) =>
        value.X >= 0f && value.Y >= 0f && value.Z >= 0f && float.IsFinite(value.X + value.Y + value.Z)
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "A color can't have a negative channel.");

    private static float Fraction(float value, string name, float most) =>
        value >= 0f && value <= most ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} must be from 0 to {most}.");

    private static float FiniteValue(float value, string name) =>
        float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} must be a real number.");

    private static Vector3 Finite(Vector3 value, string name) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} must be a real number on every axis.");
}
