using System.Numerics;
using Super;

namespace Reef;

/// <summary>Corals and seagrass grown in code, each at its real size in metres, standing on zero.</summary>
internal static class Corals
{
    /// <summary>
    /// Staghorn coral: a bush about a metre across of thick branches that fork as they reach up and out. The branches are
    /// thicker than a real staghorn's, since thin ones fall into their own shadow and read as dark twigs.
    /// </summary>
    public static GeneratedMesh Staghorn(Surface surface, RandomStream random)
    {
        var parts = new MeshParts(1f / (surface.TileWidth ?? 1f));
        int trunks = random.Between(5, 7);
        for (int i = 0; i < trunks; i++)
        {
            var spot = random.InsideCircle(0.15f);
            var lean = random.InsideCircle(1f);
            var heading = Vector3.Normalize(new Vector3(lean.X, random.Between(0.6f, 1.2f), lean.Y));
            Fork(parts, new Shoot(new Vector3(spot.X, -0.05f, spot.Y), heading, random.Between(0.35f, 0.5f), 0.05f), forks: 2, random);
        }

        return parts.Build(surface);
    }

    /// <summary>Table coral: a wide, thin plate, sometimes two, on a short thick stalk.</summary>
    public static GeneratedMesh Table(Surface surface, RandomStream random)
    {
        float uvPerMetre = 1f / (surface.TileWidth ?? 1f);
        var parts = new MeshParts(uvPerMetre);
        float height = random.Between(0.3f, 0.5f);
        parts.Branch([new(new Vector3(0f, -0.05f, 0f), 0.09f), new(new Vector3(0f, height * 0.5f, 0f), 0.07f), new(new Vector3(0.02f, height, 0f), 0.06f)], sides: 10);

        int plates = random.Between(1, 2);
        for (int i = 0; i < plates; i++)
        {
            float radius = random.Between(0.5f, 0.8f) * (1f - i * 0.35f);
            var plate = Shapes.Cylinder(topRadius: radius, bottomRadius: radius * 0.8f, height: 0.05f, segments: 40, surface: surface, uvPerMetre: uvPerMetre);
            var tilt = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(random.Between(-1f, 1f), 0f, random.Between(-1f, 1f))), random.Between(0f, 0.12f));
            var place = new Vector3(random.Between(-0.1f, 0.1f), height + i * random.Between(0.18f, 0.28f), random.Between(-0.1f, 0.1f));
            parts.Add(plate, Matrix4x4.CreateFromQuaternion(tilt) * Matrix4x4.CreateTranslation(place));
        }

        return parts.Build(surface);
    }

    /// <summary>
    /// A sea fan: a flat lace of thin branches about a metre high, forking again and again across the x-y plane, so it
    /// faces along z.
    /// </summary>
    public static GeneratedMesh SeaFan(Surface surface, RandomStream random)
    {
        var parts = new MeshParts(1f / (surface.TileWidth ?? 1f));
        var stem = new Vector3(0f, random.Between(0.15f, 0.25f), 0f);
        parts.Branch([new(new Vector3(0f, -0.05f, 0f), 0.02f), new(stem, 0.016f)], sides: 6);
        int arms = random.Between(3, 4);
        for (int i = 0; i < arms; i++)
        {
            float angle = (i + 0.5f) / arms * 1.6f - 0.8f + random.Between(-0.1f, 0.1f);
            var heading = new Vector3(MathF.Sin(angle), MathF.Cos(angle), 0f);
            Fan(parts, new Shoot(stem, heading, random.Between(0.18f, 0.26f), 0.012f), forks: 5, random);
        }

        return parts.Build(surface);
    }

    /// <summary>A tuft of seagrass: a hundred or so thin blades, leaning and curling, rooted on zero.</summary>
    public static GeneratedMesh Seagrass(Surface surface, RandomStream random)
    {
        var parts = new MeshParts(1f / (surface.TileWidth ?? 1f));
        int blades = random.Between(70, 120);
        for (int i = 0; i < blades; i++)
        {
            var spot = random.InsideCircle(0.7f);
            float length = random.Between(0.25f, 0.6f);
            float width = random.Between(0.005f, 0.008f);
            var lean = random.InsideCircle(0.5f);
            float bend = random.Between(0.2f, 0.9f);
            var across = Vector3.Normalize(new Vector3(-lean.Y + 0.01f, 0f, lean.X + 0.01f));
            var spine = new Joint[6];
            for (int j = 0; j < spine.Length; j++)
            {
                float t = j / (spine.Length - 1f);
                var sideways = new Vector3(lean.X, 0f, lean.Y) * (t * t * bend * length * 2f);
                spine[j] = new Joint(new Vector3(spot.X, t * length * (1f - 0.3f * bend * t), spot.Y) + sideways, width * (1f - 0.7f * t * t));
            }

            parts.Strip(spine, across);
        }

        return parts.Build(surface);
    }

    /// <summary>One branch of staghorn, bending a little as it grows, then forking into two or three thinner ones.</summary>
    private static void Fork(MeshParts parts, Shoot shoot, int forks, RandomStream random)
    {
        var spine = new Joint[6];
        var position = shoot.Start;
        var heading = shoot.Heading;
        for (int i = 0; i < spine.Length; i++)
        {
            spine[i] = new Joint(position, shoot.Radius * (1f - 0.25f * i / (spine.Length - 1f)));
            heading = Vector3.Normalize(heading + random.InsideSphere(0.08f) + Vector3.UnitY * 0.05f);
            position += heading * (shoot.Length / (spine.Length - 1));
        }

        parts.Branch(spine, sides: 8);
        if (forks == 0)
        {
            return;
        }

        int children = random.Between(2, 3);
        for (int i = 0; i < children; i++)
        {
            var sideways = random.InsideSphere(0.9f);
            var spread = Vector3.Normalize(heading + new Vector3(sideways.X, 0.1f, sideways.Z));
            Fork(parts, new Shoot(spine[^1].Position, spread, shoot.Length * random.Between(0.7f, 0.9f), spine[^1].Radius * 0.85f), forks - 1, random);
        }
    }

    /// <summary>One branch of a sea fan, kept in the fan's plane, forking in two until it is thin.</summary>
    private static void Fan(MeshParts parts, Shoot shoot, int forks, RandomStream random)
    {
        var (start, heading, length, radius) = shoot;
        var end = start + heading * length;
        var middle = (start + end) * 0.5f + new Vector3(random.Between(-0.02f, 0.02f), 0f, random.Between(-0.01f, 0.01f));
        parts.Branch([new(start, radius), new(middle, radius * 0.9f), new(end, radius * 0.8f)], sides: 5);
        if (forks == 0)
        {
            return;
        }

        float angle = MathF.Atan2(heading.X, heading.Y);
        for (int side = -1; side <= 1; side += 2)
        {
            // Held within about 70 degrees of straight up, so the outer branches don't droop.
            float turn = Math.Clamp(angle + side * random.Between(0.2f, 0.45f), -1.2f, 1.2f);
            var next = new Vector3(MathF.Sin(turn), MathF.Cos(turn), 0f);
            Fan(parts, new Shoot(end, next, length * random.Between(0.72f, 0.85f), MathF.Max(radius * 0.78f, 0.004f)), forks - 1, random);
        }
    }

    /// <summary>Where a branch starts, which way it grows, how long it is and how thick at its base.</summary>
    private readonly record struct Shoot(Vector3 Start, Vector3 Heading, float Length, float Radius);
}
