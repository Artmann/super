using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>
/// The village's materials: flat, matte colours, no textures. Most of it is light grey
/// plaster with a faint mottle, and a few surfaces are strongly coloured, so the light's
/// own colour is what shows.
/// </summary>
internal sealed class Palette
{
    /// <summary>How brightly a lamp's bulb glows when lit, before the flicker.</summary>
    public const float BulbStrength = 40f;

    /// <summary>How brightly a lit window glows.</summary>
    public const float WindowStrength = 4f;

    /// <summary>The warm colour of lamplight, for bulbs and windows alike.</summary>
    public static readonly Vector3 Lamplight = new(1f, 0.62f, 0.3f);

    /// <summary>The roof colours a building can have.</summary>
    public static readonly Vector3[] Roofs =
    [
        new(0.55f, 0.2f, 0.13f),
        new(0.42f, 0.26f, 0.18f),
        new(0.3f, 0.3f, 0.33f),
        new(0.5f, 0.34f, 0.18f),
    ];

    /// <summary>The strong colours: blankets, doors, a painted wall, a well's roof.</summary>
    public static readonly Vector3[] Accents =
    [
        new(0.62f, 0.06f, 0.05f),
        new(0.05f, 0.24f, 0.55f),
        new(0.75f, 0.5f, 0.04f),
        new(0.04f, 0.4f, 0.32f),
    ];

    /// <summary>
    /// The roof and strong colours of one variant of building: each cottage layout, then the
    /// workshop, the tower and the well. A variant always takes the same pair, so every copy
    /// of it draws the same and shares one bake. Variant n takes roof n and strong colour
    /// n + 1 round the lists, and from the fifth on the strong colour steps one further, so
    /// no two of the first nine pairs match.
    /// </summary>
    public static (Vector3 Roof, Vector3 Accent) Colours(int variant) =>
        (Roofs[variant % Roofs.Length], Accents[(variant + 1 + variant / Roofs.Length) % Accents.Length]);

    public Palette(Content content)
    {
        var glow = content.Shader("glow");
        Glow = new Surface
        {
            Color = new Vector3(0.18f, 0.17f, 0.15f),
            Roughness = 0.3f,
            Shader = glow,
            Parameters = [new Vector4(Lamplight, BulbStrength)],
        };
        Pane = Glow with { Color = new Vector3(0.03f, 0.03f, 0.035f), Parameters = [new Vector4(Lamplight, WindowStrength)] };
        Embers = Glow with { Color = new Vector3(0.05f, 0.03f, 0.02f), Parameters = [new Vector4(1f, 0.35f, 0.08f, 12f)] };
    }

    /// <summary>Walls, ceilings and the low garden walls: light grey with a faint mottle.</summary>
    public Surface Plaster { get; } = new() { Color = new Vector3(0.74f, 0.73f, 0.7f), Roughness = 0.92f, WorldVariation = true };

    /// <summary>Beams, furniture, posts and window bars.</summary>
    public Surface Wood { get; } = new() { Color = new Vector3(0.3f, 0.2f, 0.13f), Roughness = 0.8f };

    /// <summary>Floors inside.</summary>
    public Surface Floor { get; } = new() { Color = new Vector3(0.45f, 0.36f, 0.26f), Roughness = 0.85f };

    /// <summary>Foundations, paving, the well and the forge.</summary>
    public Surface Stone { get; } = new() { Color = new Vector3(0.5f, 0.49f, 0.47f), Roughness = 0.95f, WorldVariation = true };

    /// <summary>Roofs, tinted per kind from <see cref="Roofs"/>.</summary>
    public Surface Roof { get; } = new() { Color = Vector3.One, Roughness = 0.85f };

    /// <summary>The strong colours, tinted per kind from <see cref="Accents"/>.</summary>
    public Surface Accent { get; } = new() { Color = Vector3.One, Roughness = 0.7f };

    /// <summary>Iron: the anvil and the lamps' frames.</summary>
    public Surface Iron { get; } = new() { Color = new Vector3(0.08f, 0.08f, 0.09f), Roughness = 0.55f, Metallic = 0.6f };

    /// <summary>Water at the bottom of a well.</summary>
    public Surface Water { get; } = new() { Color = new Vector3(0.02f, 0.03f, 0.035f), Roughness = 0.1f };

    /// <summary>A lamp's bulb, which glows when its draw is tinted above 1.</summary>
    public Surface Glow { get; }

    /// <summary>A window pane that glows with the lamplight inside.</summary>
    public Surface Pane { get; }

    /// <summary>A forge's coals.</summary>
    public Surface Embers { get; }
}
