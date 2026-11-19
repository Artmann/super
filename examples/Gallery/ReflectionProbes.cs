using System.Numerics;
using Super;

namespace Gallery;

/// <summary>
/// A reflection probe in every room, the corridor and the courtyard, each boxed to its walls, so a shiny
/// thing or the glass screen reflects the space it's in.
/// </summary>
public static class ReflectionProbes
{
    /// <summary>Spawns them all, each where it's photographed from, with its box from wall to wall in the world.</summary>
    public static void Spawn(World world)
    {
        Room(world, new Vector3(-6.5f, 2.5f, -15f), new Vector3(-11f, 0f, -19.85f), new Vector3(-2.3f, 7f, -10.15f)); // whale
        Room(world, new Vector3(-6.5f, 2f, -5f), new Vector3(-11f, 0f, -9.85f), new Vector3(-2.3f, 7f, -0.3f)); // colour
        Room(world, new Vector3(-6.5f, 1.5f, -25f), new Vector3(-11f, 0f, -29.85f), new Vector3(-2.3f, 4.5f, -20.15f)); // lamps
        Room(world, new Vector3(0f, 2f, 5f), new Vector3(-6f, 0f, 0f), new Vector3(6f, 6f, 10f)); // hall
        Room(world, new Vector3(-6.5f, 2f, -36f), new Vector3(-11f, 0f, -42f), new Vector3(-2.3f, 8f, -30.15f)); // chapel
        Room(world, new Vector3(0.3f, 2f, -21f), new Vector3(-2f, 0f, -42.3f), new Vector3(2.6f, 4.5f, -0.3f)); // corridor
        Room(world, new Vector3(2f, 2f, -46f), new Vector3(-11.3f, 0f, -56f), new Vector3(7.3f, 12f, -42.3f)); // courtyard
    }

    // A probe at position, whose box runs from min to max in the world. Its own box is measured from where it stands.
    private static void Room(World world, Vector3 position, Vector3 min, Vector3 max) =>
        world.Spawn(Transform.At(position), new ReflectionCapture(min - position, max - position));
}
