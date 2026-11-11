using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// Wind worked out the way wind.glsl's windSway does, so it can be tested here. A vertex of a material with
/// <see cref="Material.Wind"/> leans downwind by the strength times the square of its height above its part's lowest
/// point, clamped to 1 m, so the foot stays put and the tips move most. Gusts, value noise that scrolls downwind, swing
/// the lean between a share of it and all of it. On top, leaves flutter quickly a little way, whatever the material's sway.
/// </summary>
internal static class Wind
{
    /// <summary>How fast gusts travel downwind, in metres a second.</summary>
    public const float GustSpeed = 0.15f;

    /// <summary>How far apart, in metres, one gust is from the next: the size of the noise's cells.</summary>
    public const float GustSize = 3f;

    /// <summary>How far, in metres, a vertex 1 m up flutters either way.</summary>
    public const float Flutter = 0.02f;

    /// <summary>How many times a second leaves flutter.</summary>
    public const float FlutterHz = 0.4f;

    /// <summary>The wind's strength at which leaves flutter fully; in a lighter wind they flutter less, in a calm not at all.</summary>
    public const float FlutterStrength = 0.25f;

    /// <summary>The height above a part's foot, in metres, past which a vertex sways no further.</summary>
    public const float FullHeight = 1f;

    /// <summary>
    /// How far a vertex moves, in metres. <paramref name="position"/> is where it stands at rest, in the world,
    /// <paramref name="heightAboveBase"/> how far it is above its part's lowest point, <paramref name="direction"/> the
    /// way the wind blows, <paramref name="strength"/> how far it leans grass 1 m tall, <paramref name="gustiness"/>
    /// from 0 for a steady wind to 1 for one that comes and goes, <paramref name="time"/> the frame's time in seconds,
    /// and <paramref name="sway"/> the material's share of the lean.
    /// </summary>
    public static Vector3 Sway(
        Vector3 position, float heightAboveBase, Vector3 direction, float strength, float gustiness, float time, float sway = 1f)
    {
        if (strength <= 0f || sway <= 0f || heightAboveBase <= 0f || direction == Vector3.Zero)
        {
            return Vector3.Zero;
        }

        var downwind = Vector3.Normalize(direction);
        float h = MathF.Min(heightAboveBase, FullHeight);
        float lean = strength * sway * h * h * GustFactor(position, downwind, gustiness, time);
        float flutter = Flutter * h * MathF.Min(strength / FlutterStrength, 1f)
            * MathF.Sin(2f * MathF.PI * FlutterHz * time + FlutterPhase(position));
        return downwind * lean + Vector3.Normalize(downwind + Vector3.UnitY) * flutter;
    }

    /// <summary>
    /// The share of the full lean the gusts give at a place and time: 1 − 0.8 × gustiness in a lull and 1 at a gust's
    /// peak, so at the default gustiness of 0.5 it runs from 0.6 to 1.
    /// </summary>
    public static float GustFactor(Vector3 position, Vector3 downwind, float gustiness, float time)
    {
        var at = new Vector2(position.X, position.Z) / GustSize - new Vector2(downwind.X, downwind.Z) * (GustSpeed / GustSize * time);
        float swing = 0.8f * gustiness;
        return 1f - swing + swing * Gust(at);
    }

    /// <summary>Where in its cycle a vertex's flutter is, so neighbouring leaves don't flutter as one.</summary>
    public static float FlutterPhase(Vector3 position) => Vector3.Dot(position, new Vector3(3.1f, 2.3f, 2.7f));

    /// <summary>Smooth value noise from 0 to 1, from a hash of each whole cell's corners.</summary>
    public static float Gust(Vector2 at)
    {
        float cellX = MathF.Floor(at.X);
        float cellY = MathF.Floor(at.Y);
        float fx = at.X - cellX;
        float fy = at.Y - cellY;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        int x = (int)cellX;
        int y = (int)cellY;
        float bottom = Lerp(Hash(x, y), Hash(x + 1, y), fx);
        float top = Lerp(Hash(x, y + 1), Hash(x + 1, y + 1), fx);
        return Lerp(bottom, top, fy);
    }

    /// <summary>A number from 0 to 1 for a cell, the same on the CPU and the GPU: a PCG hash of its two coordinates.</summary>
    public static float Hash(int x, int y)
    {
        unchecked
        {
            uint state = ((uint)x * 73856093u) ^ ((uint)y * 19349663u);
            state = state * 747796405u + 2891336453u;
            uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            word = (word >> 22) ^ word;
            return word / 4294967295f;
        }
    }

    /// <summary>
    /// What the frame block gets: xyz the way the wind blows, flattened onto the ground, times its strength, and w the
    /// gustiness. A wind blowing straight up or down has no way to go along the ground, so it packs as a calm.
    /// </summary>
    public static Vector4 Pack(Vector3 direction, float strength, float gustiness)
    {
        var flat = new Vector3(direction.X, 0f, direction.Z);
        if (flat.LengthSquared() < 1e-12f)
        {
            return Vector4.Zero;
        }

        return new Vector4(Vector3.Normalize(flat) * strength, gustiness);
    }

    /// <summary>The frame block's wind: all zero, which keeps everything still, with none set or the switch off.</summary>
    public static Vector4 Pack(WindSettings? settings, RenderEffects off) =>
        settings is { } wind && (off & RenderEffects.Wind) == 0 && wind.Strength > 0f
            ? Pack(wind.Direction, wind.Strength, wind.Gustiness)
            : Vector4.Zero;

    /// <summary>How strong the frame's wind is, as the shaders get it: 0 with none set, the switch off, or a wind with no way along the ground.</summary>
    public static float Strength(WindSettings? settings, RenderEffects off)
    {
        var packed = Pack(settings, off);
        return new Vector3(packed.X, packed.Y, packed.Z).Length();
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
