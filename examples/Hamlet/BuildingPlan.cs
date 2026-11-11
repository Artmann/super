using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>Which of a building's colours a part takes when it's drawn.</summary>
internal enum PartTint
{
    /// <summary>The material's own colour.</summary>
    None,

    /// <summary>The building's roof colour.</summary>
    Roof,

    /// <summary>The building's strong colour.</summary>
    Accent,

    /// <summary>Glows with the building's lamps.</summary>
    Lamps,

    /// <summary>Glows with a forge's fire, day and night.</summary>
    Fire,
}

/// <summary>One mesh of a building, with one material.</summary>
internal readonly record struct Part(ModelRef Model, PartTint Tint);

/// <summary>
/// A lamp in a building's own space. Lamps are spot lights: they cost a sixth of a point
/// light's shadow. A fire burns all day; the rest come on at dusk.
/// </summary>
internal readonly record struct Lamp(
    Vector3 Position,
    Vector3 Direction,
    Vector3 Color,
    float Range,
    float InnerAngle,
    float OuterAngle,
    bool Fire = false);

/// <summary>
/// A window in a building's own space: the middle of its opening on the outside face of the
/// wall, which way the wall faces, and the opening's width and height.
/// </summary>
internal readonly record struct Window(Vector3 Middle, Vector3 Outward, Vector2 Size);

/// <summary>
/// One kind of building, built once and placed many times. Everything is in the building's
/// own space: the floor is at height zero, the door faces +z, and <see cref="Min"/> and
/// <see cref="Max"/> box all of it. That box under one placement transform is the piece a
/// later bake would treat as a unit.
/// </summary>
internal sealed record BuildingPlan(
    string Name,
    Part[] Parts,
    Lamp[] Lamps,
    Window[] Windows,
    Vector3 Min,
    Vector3 Max,
    Vector3 Door,
    Vector3 Inside)
{
    /// <summary>The middle of the box on the ground, in the building's own space.</summary>
    public Vector3 Middle => new((Min.X + Max.X) * 0.5f, 0f, (Min.Z + Max.Z) * 0.5f);

    /// <summary>How far the box reaches from its middle across the ground.</summary>
    public float Reach => new Vector2(Max.X - Min.X, Max.Z - Min.Z).Length() * 0.5f;
}

/// <summary>A gap in a wall: a door when it starts at the floor, else a window.</summary>
internal readonly record struct Opening(float Along, float Width, float Bottom, float Top, bool Window = true);

/// <summary>
/// Collects a building's meshes, one per material, and its lamps and windows, and turns
/// them into a <see cref="BuildingPlan"/>.
/// </summary>
internal sealed class PlanBuilder(Palette palette)
{
    private readonly List<Lamp> _lamps = [];
    private readonly List<Window> _windows = [];

    public Palette Palette { get; } = palette;

    public MeshBuilder Plaster { get; } = new();

    public MeshBuilder Wood { get; } = new();

    public MeshBuilder Floor { get; } = new();

    public MeshBuilder Stone { get; } = new();

    public MeshBuilder Roof { get; } = new();

    public MeshBuilder Accent { get; } = new();

    public MeshBuilder Iron { get; } = new();

    public MeshBuilder Water { get; } = new();

    public MeshBuilder Bulbs { get; } = new();

    public MeshBuilder Embers { get; } = new();

    public void Lamp(Lamp lamp) => _lamps.Add(lamp);

    /// <summary>
    /// A hanging lamp: a cord from the ceiling, a small iron cap and a glowing bulb, and a
    /// spot light shining down from it.
    /// </summary>
    public void HangingLamp(Vector3 bulb, float ceiling, Vector3 color, float range)
    {
        float cord = ceiling - bulb.Y - 0.1f;
        if (cord > 0.01f)
        {
            Iron.Box(new Vector3(0.025f, cord, 0.025f), bulb + new Vector3(0f, 0.1f + cord * 0.5f, 0f));
        }

        Iron.Add(Shapes.Cone(0.16f, 0.1f, 12), Matrix4x4.CreateTranslation(bulb + new Vector3(0f, 0.08f, 0f)));
        Bulbs.Add(Shapes.Sphere(0.07f, 12, 6), Matrix4x4.CreateTranslation(bulb));
        Lamp(new Lamp(bulb - new Vector3(0f, 0.08f, 0f), -Vector3.UnitY, color, range, 0.55f, 1.35f));
    }

    /// <summary>
    /// A straight wall. The frame puts its middle at the origin, running along x and facing
    /// out along +z; openings are measured along x from that middle.
    /// </summary>
    public void Wall(Matrix4x4 frame, float length, float thickness, float bottom, float top, params Opening[] openings)
    {
        float half = length * 0.5f;
        float depth = thickness * 0.5f;
        var sorted = openings.OrderBy(opening => opening.Along).ToArray();
        float x = -half;
        foreach (var opening in sorted)
        {
            float left = opening.Along - opening.Width * 0.5f;
            float right = opening.Along + opening.Width * 0.5f;
            Plaster.Span(new Vector3(x, bottom, -depth), new Vector3(left, top, depth), frame);
            Plaster.Span(new Vector3(left, bottom, -depth), new Vector3(right, opening.Bottom, depth), frame);
            Plaster.Span(new Vector3(left, opening.Top, -depth), new Vector3(right, top, depth), frame);
            x = right;

            float height = opening.Top - opening.Bottom;
            float middle = (opening.Bottom + opening.Top) * 0.5f;
            if (opening.Window)
            {
                // A cross of bars, and a sill that stands out from the wall.
                Wood.Span(new Vector3(opening.Along - 0.03f, opening.Bottom, -0.04f), new Vector3(opening.Along + 0.03f, opening.Top, 0.04f), frame);
                Wood.Span(new Vector3(left, middle + 0.08f, -0.04f), new Vector3(right, middle + 0.14f, 0.04f), frame);
                Wood.Span(new Vector3(left - 0.06f, opening.Bottom - 0.06f, depth - 0.02f), new Vector3(right + 0.06f, opening.Bottom, depth + 0.08f), frame);
                var outside = Vector3.Transform(new Vector3(opening.Along, middle, depth), frame);
                var outward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, frame));
                _windows.Add(new Window(outside, outward, new Vector2(opening.Width, height)));
            }
            else
            {
                // A wooden frame round the door.
                Wood.Span(new Vector3(left - 0.08f, opening.Bottom, depth - 0.02f), new Vector3(left, opening.Top + 0.08f, depth + 0.04f), frame);
                Wood.Span(new Vector3(right, opening.Bottom, depth - 0.02f), new Vector3(right + 0.08f, opening.Top + 0.08f, depth + 0.04f), frame);
                Wood.Span(new Vector3(left - 0.08f, opening.Top, depth - 0.02f), new Vector3(right + 0.08f, opening.Top + 0.08f, depth + 0.04f), frame);
            }
        }

        Plaster.Span(new Vector3(x, bottom, -depth), new Vector3(half, top, depth), frame);
    }

    /// <summary>The frame for a wall whose middle is at a point and which faces out along a direction on the ground.</summary>
    public static Matrix4x4 Facing(Vector3 middle, Vector3 outward) =>
        Matrix4x4.CreateRotationY(MathF.Atan2(outward.X, outward.Z)) * Matrix4x4.CreateTranslation(middle);

    /// <summary>Uploads the meshes and gathers everything into a plan.</summary>
    public BuildingPlan Build(Content content, string name, Vector3 min, Vector3 max, Vector3 door, Vector3 inside)
    {
        var parts = new List<Part>();
        void Add(MeshBuilder mesh, Surface surface, PartTint tint)
        {
            if (!mesh.IsEmpty)
            {
                parts.Add(new Part(content.Mesh(mesh.Build(surface)), tint));
            }
        }

        Add(Plaster, Palette.Plaster, PartTint.None);
        Add(Wood, Palette.Wood, PartTint.None);
        Add(Floor, Palette.Floor, PartTint.None);
        Add(Stone, Palette.Stone, PartTint.None);
        Add(Roof, Palette.Roof, PartTint.Roof);
        Add(Accent, Palette.Accent, PartTint.Accent);
        Add(Iron, Palette.Iron, PartTint.None);
        Add(Water, Palette.Water, PartTint.None);
        Add(Bulbs, Palette.Glow, PartTint.Lamps);
        Add(Embers, Palette.Embers, PartTint.Fire);
        return new BuildingPlan(name, [.. parts], [.. _lamps], [.. _windows], min, max, door, inside);
    }
}
