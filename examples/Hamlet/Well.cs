using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>
/// A well in a small paved yard: a low wall round three sides, the well under a little
/// roof in the strong colour, a bench, and two lantern posts at the open side.
/// </summary>
internal static class Well
{
    private const float Half = 3.4f;
    private const float PaveTop = 0.12f;

    public static BuildingPlan Build(Content content, Palette palette)
    {
        var b = new PlanBuilder(palette);

        b.Stone.Span(new Vector3(-Half, -1.2f, -Half), new Vector3(Half, PaveTop, Half), Matrix4x4.Identity);
        // Paving slabs, a little proud of the yard, with joints between them.
        for (int x = 0; x < 6; x++)
        {
            for (int z = 0; z < 6; z++)
            {
                float x0 = -Half + 0.1f + x * 1.12f;
                float z0 = -Half + 0.1f + z * 1.12f;
                b.Stone.Span(new Vector3(x0, PaveTop, z0), new Vector3(x0 + 1.06f, PaveTop + 0.03f, z0 + 1.06f), Matrix4x4.Identity);
            }
        }

        float wall = 0.7f;
        b.Plaster.Span(new Vector3(-Half, PaveTop, -Half), new Vector3(Half, PaveTop + wall, -Half + 0.3f), Matrix4x4.Identity);
        b.Plaster.Span(new Vector3(-Half, PaveTop, -Half + 0.3f), new Vector3(-Half + 0.3f, PaveTop + wall, Half - 1.2f), Matrix4x4.Identity);
        b.Plaster.Span(new Vector3(Half - 0.3f, PaveTop, -Half + 0.3f), new Vector3(Half, PaveTop + wall, Half - 1.2f), Matrix4x4.Identity);

        // The well: a stone ring with dark water in it.
        b.Stone.Add(Shapes.Cylinder(0.9f, 0.95f, 0.8f, 20), Matrix4x4.CreateTranslation(0f, PaveTop + 0.4f, 0f));
        b.Water.Add(Shapes.Cylinder(0.7f, 0.7f, 0.02f, 20), Matrix4x4.CreateTranslation(0f, PaveTop + 0.81f, 0f));
        foreach (float x in (ReadOnlySpan<float>)[-0.85f, 0.85f])
        {
            b.Wood.Span(new Vector3(x - 0.07f, PaveTop + 0.4f, -0.07f), new Vector3(x + 0.07f, PaveTop + 2.3f, 0.07f), Matrix4x4.Identity);
        }

        b.Wood.Add(Shapes.Cylinder(0.06f, 0.06f, 1.9f, 10), Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, PaveTop + 1.7f, 0f));
        b.Iron.Span(new Vector3(-0.01f, PaveTop + 1.0f, -0.01f), new Vector3(0.01f, PaveTop + 1.7f, 0.01f), Matrix4x4.Identity);
        b.Wood.Add(Shapes.Cylinder(0.14f, 0.12f, 0.22f, 10), Matrix4x4.CreateTranslation(0f, PaveTop + 0.95f, 0f));
        var roof = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, PaveTop + 2.3f, 0f);
        b.Accent.PitchedRoof(0.85f, 0.7f, 1.6f, 0.25f, 0.08f, roof);

        // A bench against the back wall.
        b.Wood.Span(new Vector3(-1.4f, PaveTop + 0.42f, -Half + 0.35f), new Vector3(1.4f, PaveTop + 0.48f, -Half + 0.8f), Matrix4x4.Identity);
        foreach (float x in (ReadOnlySpan<float>)[-1.25f, 1.25f])
        {
            b.Wood.Span(new Vector3(x - 0.05f, PaveTop, -Half + 0.4f), new Vector3(x + 0.05f, PaveTop + 0.42f, -Half + 0.75f), Matrix4x4.Identity);
        }

        // Two lantern posts by the open side, each with a lamp hanging from an arm.
        var light = new Vector3(3.2f, 2.0f, 0.95f);
        foreach (float side in (ReadOnlySpan<float>)[-1f, 1f])
        {
            var foot = new Vector3(side * (Half - 0.3f), PaveTop, Half - 0.3f);
            b.Wood.Span(foot + new Vector3(-0.08f, 0f, -0.08f), foot + new Vector3(0.08f, 2.7f, 0.08f), Matrix4x4.Identity);
            b.Wood.Span(foot + new Vector3(-side * 0.7f, 2.55f, -0.05f), foot + new Vector3(0f, 2.63f, 0.05f), Matrix4x4.Identity);
            var bulb = foot + new Vector3(-side * 0.55f, 2.25f, 0f);
            b.Iron.Add(Shapes.Cone(0.14f, 0.12f, 10), Matrix4x4.CreateTranslation(bulb + new Vector3(0f, 0.12f, 0f)));
            b.Iron.Span(bulb + new Vector3(-0.01f, 0.15f, -0.01f), bulb + new Vector3(0.01f, 0.32f, 0.01f), Matrix4x4.Identity);
            b.Bulbs.Add(Shapes.Sphere(0.08f, 12, 6), Matrix4x4.CreateTranslation(bulb));
            b.Lamp(new Lamp(bulb - new Vector3(0f, 0.1f, 0f), -Vector3.UnitY, light, 9f, 0.6f, 1.35f));
        }

        return b.Build(
            content,
            "well",
            new Vector3(-Half, -1.2f, -Half),
            new Vector3(Half, PaveTop + 3.2f, Half),
            new Vector3(0f, 0f, Half + 1.5f),
            new Vector3(0f, PaveTop, Half - 1.5f));
    }
}
