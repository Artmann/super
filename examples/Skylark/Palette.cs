using System.Numerics;

namespace SkylarkExample;

/// <summary>The colours Skylark is painted with, as red, green and blue.</summary>
public static class Palette
{
    public static readonly Vector3 Grass = new(0.3f, 0.62f, 0.18f);
    public static readonly Vector3 Soil = new(0.52f, 0.37f, 0.27f);
    public static readonly Vector3 Rock = new(0.5f, 0.47f, 0.55f);
    public static readonly Vector3 Plank = new(0.8f, 0.62f, 0.4f);
    public static readonly Vector3 Wood = new(0.62f, 0.44f, 0.28f);
    public static readonly Vector3 Leaves = new(0.16f, 0.45f, 0.24f);
    public static readonly Vector3 Feathers = new(1f, 0.72f, 0.15f);
    public static readonly Vector3 Wing = new(0.95f, 0.55f, 0.12f);
    public static readonly Vector3 Beak = new(0.95f, 0.5f, 0.2f);
    public static readonly Vector3 Ink = new(0.1f, 0.08f, 0.08f);
    public static readonly Vector3 Jewel = new(0.35f, 0.85f, 0.95f);
}
