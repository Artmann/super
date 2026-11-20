using System.Numerics;
using Super;

namespace SkylarkExample;

/// <summary>
/// The shapes Skylark draws with, toon shaded with an ink outline. Each is white, so each part tints it with its
/// own color.
/// </summary>
public sealed class Look(Content content)
{
    public ModelRef Cube { get; private set; }

    public ModelRef Sphere { get; private set; }

    public ModelRef Cone { get; private set; }

    public ModelRef Cylinder { get; private set; }

    /// <summary>Eight flat faces coming to a point at the top and bottom.</summary>
    public ModelRef Gem { get; private set; }

    /// <summary>Makes the shapes. Call it once, from the game's Load.</summary>
    public void Load()
    {
        var cartoon = new Surface
        {
            Color = Vector3.One,
            Roughness = 0.9f,
            Toon = new Toon(Steps: 2, Rim: new Vector3(0.15f)),
            Features = SurfaceFeatures.Outline,
        };

        Cube = content.Mesh(Shapes.Box(Vector3.One, surface: cartoon));
        Sphere = content.Mesh(Shapes.Sphere(surface: cartoon));
        Cone = content.Mesh(Shapes.Cone(surface: cartoon));
        Cylinder = content.Mesh(Shapes.Cylinder(surface: cartoon));

        // A sphere with four sides and two rings is a gem. Flat gives each face its own shade.
        Gem = content.Mesh(Shapes.Flat(Shapes.Sphere(segments: 4, rings: 2, surface: cartoon with { Roughness = 0.3f })));
    }
}
