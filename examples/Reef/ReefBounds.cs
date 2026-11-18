using System.Numerics;

namespace Reef;

/// <summary>The water the fish keep to: inside the reef's circle, above the seabed and below the surface.</summary>
internal sealed class ReefBounds(ReefSettings settings)
{
    private const float BoundsWeight = 2.5f;
    private const float GroundClearance = 2.5f;
    private const float SurfaceClearance = 3f;

    /// <summary>A push back towards the open water, for a fish near an edge.</summary>
    public Vector3 AvoidEdges(Vector3 position)
    {
        var push = Vector3.Zero;
        float sideways = MathF.Sqrt(position.X * position.X + position.Z * position.Z);
        float outside = sideways - (settings.Radius - 8f);
        if (outside > 0f)
        {
            push -= new Vector3(position.X, 0f, position.Z) / sideways * outside * BoundsWeight;
        }

        // Most fish are too high to need the seabed's exact height.
        if (position.Y < Seabed.Highest + GroundClearance)
        {
            float ground = Seabed.Height(position.X, position.Z) + GroundClearance;
            if (position.Y < ground)
            {
                push.Y += (ground - position.Y) * BoundsWeight * 2f;
            }
        }

        float ceiling = settings.SurfaceHeight - SurfaceClearance;
        if (position.Y > ceiling)
        {
            push.Y -= (position.Y - ceiling) * BoundsWeight * 2f;
        }

        return push;
    }

    /// <summary>The last resort, for a fish that steering didn't turn in time.</summary>
    public Vector3 Keep(Vector3 position)
    {
        float sideways = MathF.Sqrt(position.X * position.X + position.Z * position.Z);
        if (sideways > settings.Radius)
        {
            position.X *= settings.Radius / sideways;
            position.Z *= settings.Radius / sideways;
        }

        if (position.Y < Seabed.Highest + 0.3f)
        {
            position.Y = MathF.Max(position.Y, Seabed.Height(position.X, position.Z) + 0.3f);
        }

        position.Y = MathF.Min(position.Y, settings.SurfaceHeight - 0.5f);
        return position;
    }
}
