using System.Numerics;
using Super;

namespace Gallery;

/// <summary>
/// A reflection probe in every room, the corridor and the courtyard, each boxed to its walls, so a shiny
/// thing or the glass screen reflects the space it's in.
/// </summary>
public static class ReflectionProbes
{
    /// <summary>Places them all. Like lights, they're placed every frame, and the keys only have to differ.</summary>
    public static void Place(View view)
    {
        view.Indirect.ReflectionProbe(1, new Vector3(-6.5f, 2.5f, -15f), new Vector3(-11f, 0f, -19.85f), new Vector3(-2.3f, 7f, -10.15f)); // whale
        view.Indirect.ReflectionProbe(2, new Vector3(-6.5f, 2f, -5f), new Vector3(-11f, 0f, -9.85f), new Vector3(-2.3f, 7f, -0.3f)); // colour
        view.Indirect.ReflectionProbe(3, new Vector3(-6.5f, 1.5f, -25f), new Vector3(-11f, 0f, -29.85f), new Vector3(-2.3f, 4.5f, -20.15f)); // lamps
        view.Indirect.ReflectionProbe(4, new Vector3(0f, 2f, 5f), new Vector3(-6f, 0f, 0f), new Vector3(6f, 6f, 10f)); // hall
        view.Indirect.ReflectionProbe(5, new Vector3(-6.5f, 2f, -36f), new Vector3(-11f, 0f, -42f), new Vector3(-2.3f, 8f, -30.15f)); // chapel
        view.Indirect.ReflectionProbe(6, new Vector3(0.3f, 2f, -21f), new Vector3(-2f, 0f, -42.3f), new Vector3(2.6f, 4.5f, -0.3f)); // corridor
        view.Indirect.ReflectionProbe(7,new Vector3(2f, 2f, -46f), new Vector3(-11.3f, 0f, -56f), new Vector3(7.3f, 12f, -42.3f)); // courtyard
    }
}
