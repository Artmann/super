using System.Numerics;
using Rendition;

namespace Super;

/// <summary>The rules a view's lights keep. Each throws naming the argument as <c>name</c>.</summary>
internal static class LightChecks
{
    /// <summary>Throws for a mode that isn't Mixed, Realtime or Baked.</summary>
    public static void Mode(LightMode mode, string name)
    {
        // Not Enum.IsDefined: a game checks this every frame for every light, and that allocates.
        if (mode < LightMode.Mixed || mode > LightMode.Baked)
        {
            throw new ArgumentOutOfRangeException(
                name,
                (int)mode,
                $"A light's mode must be Mixed, Realtime or Baked, but it was {(int)mode}.");
        }
    }

    /// <summary>Throws for a point or spot light that can't be drawn, or for one past the most a frame holds.</summary>
    public static void Local(in LocalLight light, int count, string name)
    {
        Mode(light.Mode, name);

        if (count >= LocalLight.MaxPerFrame)
        {
            throw new InvalidOperationException(
                $"A frame can have at most {LocalLight.MaxPerFrame} point and spot lights. Leave out those too far away to see.");
        }

        Reach(light.Range, light.Radius, name);
        Look(light.Position, light.Radiance, light.Volumetric, name);
    }

    /// <summary>Throws for a spot light with no direction, or with angles that make no cone.</summary>
    public static void Cone(Vector3 direction, float innerAngle, float outerAngle, string name)
    {
        if (!(direction.LengthSquared() > 1e-12f) || !float.IsFinite(direction.LengthSquared()))
        {
            throw new ArgumentException($"A spot light needs a direction to shine in, not {direction}.", name);
        }

        if (!(outerAngle > 0f && outerAngle < MathF.PI / 2f))
        {
            throw new ArgumentOutOfRangeException(
                name, outerAngle, "A spot light's outer angle must be more than 0 and less than π/2 radians.");
        }

        if (!(innerAngle >= 0f && innerAngle <= outerAngle))
        {
            throw new ArgumentOutOfRangeException(
                name, innerAngle, "A spot light's inner angle must be from 0 up to its outer angle.");
        }
    }

    private static void Reach(float range, float radius, string name)
    {
        if (!(range > 0f) || !float.IsFinite(range))
        {
            throw new ArgumentOutOfRangeException(name, range, "A light's range must be more than 0 metres.");
        }

        if (!(radius > 0f && radius < range))
        {
            throw new ArgumentOutOfRangeException(
                name, radius, "A light's radius must be more than 0 metres and less than its range.");
        }
    }

    private static void Look(Vector3 position, Vector3 color, float volumetric, string name)
    {
        if (!float.IsFinite(position.X + position.Y + position.Z))
        {
            throw new ArgumentException($"A light can't be at {position}.", name);
        }

        if (!(volumetric >= 0f) || !float.IsFinite(volumetric))
        {
            throw new ArgumentOutOfRangeException(
                name, volumetric, "A light's volumetric strength must be 0 or more, and 0 leaves it out of the fog.");
        }

        if (!(color.X >= 0f && color.Y >= 0f && color.Z >= 0f) || !float.IsFinite(color.X + color.Y + color.Z))
        {
            throw new ArgumentException($"A light's color can't be negative, but it was {color}.", name);
        }
    }
}
