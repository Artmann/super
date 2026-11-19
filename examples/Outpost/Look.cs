using System.Numerics;
using Super;

namespace OutpostExample;

/// <summary>
/// The shapes Outpost draws with, made toon shaded with an ink outline. Each is white and the same size as the
/// built-in shape it stands in for, so each part tints it with its own color and places it the same way.
/// </summary>
public sealed class Look(Content content)
{
    public ModelRef Cube { get; private set; }

    /// <summary>A cube with no outline, for the tiles.</summary>
    public ModelRef Tile { get; private set; }

    public ModelRef Sphere { get; private set; }

    public ModelRef Capsule { get; private set; }

    public ModelRef Cylinder { get; private set; }

    public ModelRef Cone { get; private set; }

    /// <summary>Makes the shapes. Call it once, from the game's Load.</summary>
    public void Load()
    {
        // Two flat bands of light, a soft rim where a shape turns away, and an ink line round it.
        var cartoon = new Surface
        {
            Color = Vector3.One,
            Roughness = 0.9f,
            Toon = new Toon(Steps: 2, Rim: new Vector3(0.15f)),
            Features = SurfaceFeatures.Outline,
        };

        Cube = content.Mesh(Shapes.Box(Vector3.One, surface: cartoon));
        // Tiles sit side by side, so an outline on each would draw a heavy grid. They go without.
        Tile = content.Mesh(Shapes.Box(Vector3.One, surface: cartoon with { Features = SurfaceFeatures.None }));
        Sphere = content.Mesh(Shapes.Sphere(surface: cartoon));
        Capsule = content.Mesh(Shapes.Capsule(surface: cartoon));
        Cylinder = content.Mesh(Shapes.Cylinder(surface: cartoon));
        Cone = content.Mesh(Shapes.Cone(surface: cartoon));
    }
}
