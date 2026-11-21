using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// Grows grass over an entity's <see cref="Model"/>, as a component: blades stand on the model's top surface, seen
/// from above, follow its height, and sway in the <see cref="Wind"/>. The model is drawn as usual under them. Left
/// empty it grows the default meadow all over; give a <paramref name="Density"/> map to say where it grows. At most
/// eight different looks show in a frame. See "Grass" in docs/rendering.md.
/// </summary>
/// <param name="Look">How the grass looks.</param>
/// <param name="Density">Where on the model the grass grows, and how thick, or null to grow it all over.</param>
public readonly record struct Grass(GrassSettings Look, GrassMap? Density = null)
{
    /// <summary>The default meadow, all over the model.</summary>
    public Grass()
        : this(new GrassSettings())
    {
    }

    /// <summary>Throws for a look out of range or a map that can't be read, naming the model by <paramref name="key"/>.</summary>
    internal void Check(int key, string name)
    {
        if (GrassSettings.Problem(Look) is { } problem)
        {
            throw new ArgumentException($"Model {key}'s grass: {problem}", name);
        }

        if (Density is { } map && GrassDensity.Problem(new GrassDensity(default, map.Min, map.Max, map.Channel)) is { } mapProblem)
        {
            throw new ArgumentException($"Model {key}'s grass: {mapProblem}", name);
        }
    }
}

/// <summary>
/// A texture that says how thick grass stands, laid flat over its model from above, for <see cref="Grass.Density"/>.
/// Outside it no grass grows.
/// </summary>
/// <param name="Map">The texture, best made <see cref="TextureKind.Linear"/>.</param>
/// <param name="Min">The x and z, in the model's own space, of the map's first texel's corner. Its u runs along x and v along z.</param>
/// <param name="Max">The x and z of its last texel's far corner. Above <paramref name="Min"/> both ways.</param>
/// <param name="Channel">Which channel says how thick the grass is, from 0 for bare to 1 for full: 0 red, 1 green, 2 blue or 3 alpha.</param>
public readonly record struct GrassMap(TextureRef Map, Vector2 Min, Vector2 Max, int Channel = 0);
