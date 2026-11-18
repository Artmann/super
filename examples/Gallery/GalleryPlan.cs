using System.Numerics;

namespace Gallery;

/// <summary>Where the sun is, and where the openings and the things the sun and the lamps light stand.</summary>
public static class GalleryPlan
{
    /// <summary>Where the sun is: high in the north-east, so it rakes through the colonnade.</summary>
    public static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.8f, 0.9f, -0.35f));

    /// <summary>The entrance hall's skylight, in the roof 6 m up.</summary>
    public static readonly Area HallSkylight = new(new(3.5f, 6f, 0.5f), new(5f, 6.3f, 7f));

    /// <summary>The colour room's clerestory window, in its east wall above the corridor roof.</summary>
    public static readonly Area ColourWindow = new(new(-2.3f, 4.9f, -8f), new(-2f, 6.6f, -5f));

    /// <summary>The whale room's skylight, in the roof 7 m up.</summary>
    public static readonly Area WhaleSkylight = new(new(-4.2f, 7f, -18.5f), new(-2.6f, 7.3f, -15.5f));

    /// <summary>The top of the bust's plinth, placed where the sun through the window lands on the bust.</summary>
    public static readonly Vector3 BustAt = Below(ColourWindow.Centre, 1.3f) with { Y = 1f };

    /// <summary>The top of the whale's plinth, placed where the sun through the skylight lands on the whale.</summary>
    public static readonly Vector3 WhaleAt = Below(WhaleSkylight.Centre, 1.05f) with { Y = 0.5f };

    /// <summary>The glass screen between the corridor and the courtyard.</summary>
    public const float ScreenZ = -42.15f;

    /// <summary>The lamp room's three lamps: the middle of each glowing disc.</summary>
    public static readonly Vector3[] Lamps = [new(-9f, 2.9f, -22.8f), new(-6.5f, 2.9f, -22.8f), new(-4f, 2.9f, -22.8f)];

    /// <summary>The first draw key of what moves. The building's pieces take the keys below it.</summary>
    public const int DynamicKey = 10_000;

    /// <summary>A box in the world, from one corner to the other.</summary>
    public readonly record struct Area(Vector3 Min, Vector3 Max)
    {
        public Vector3 Centre => (Min + Max) / 2f;
    }

    /// <summary>Where the sun through an opening lands at a height.</summary>
    private static Vector3 Below(Vector3 opening, float height) =>
        opening - SunDirection * ((opening.Y - height) / SunDirection.Y);
}
