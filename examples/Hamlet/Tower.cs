using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>
/// A round plastered tower of two floors on a stone footing under a cone roof, with a band in the strong colour
/// between them. Its wall is a ring of blocks, and a window or the door is a block left
/// out. A lamp hangs on each floor.
/// </summary>
internal static class Tower
{
    private const int Segments = 16;
    private const float Radius = 2.2f;
    private const float Thickness = 0.4f;
    private const float FloorTop = 0.12f;
    private const float UpperFloor = 3.9f;
    private const float WallTop = 8.2f;

    public static BuildingPlan Build(Content content, Palette palette)
    {
        var b = new PlanBuilder(palette);
        float inner = Radius - Thickness * 0.5f;
        float outer = Radius + Thickness * 0.5f;
        float block = 2f * outer * MathF.Sin(MathF.PI / Segments) + 0.02f;

        for (int i = 0; i < Segments; i++)
        {
            // Block 0 faces +z and holds the door; a few others hold windows on either floor.
            float angle = 2f * MathF.PI * i / Segments;
            var outward = new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
            var frame = PlanBuilder.Facing(outward * Radius, outward);
            var openings = new List<Opening>();
            if (i == 0)
            {
                openings.Add(new Opening(0f, 0.8f, FloorTop, 2.1f, Window: false));
            }

            if (i is 4 or 11)
            {
                openings.Add(new Opening(0f, 0.55f, 1.1f, 2.2f));
            }

            if (i is 2 or 7 or 10 or 14)
            {
                openings.Add(new Opening(0f, 0.55f, UpperFloor + 1.0f, UpperFloor + 2.1f));
            }

            b.Wall(frame, block, Thickness, -1.2f, WallTop, [.. openings]);
        }

        // The footing, floors, a ceiling and the cone roof, with a band in the strong colour where the upper floor is.
        b.Stone.Add(Shapes.Cylinder(outer + 0.15f, outer + 0.15f, 1.4f, Segments), Matrix4x4.CreateTranslation(0f, -0.7f, 0f));
        b.Floor.Add(Shapes.Cylinder(inner + 0.05f, inner + 0.05f, 0.04f, Segments), Matrix4x4.CreateTranslation(0f, FloorTop + 0.02f, 0f));
        b.Floor.Add(Shapes.Cylinder(inner + 0.05f, inner + 0.05f, 0.18f, Segments), Matrix4x4.CreateTranslation(0f, UpperFloor - 0.09f, 0f));
        b.Plaster.Add(Shapes.Cylinder(outer, outer, 0.2f, Segments), Matrix4x4.CreateTranslation(0f, WallTop + 0.1f, 0f));
        b.Accent.Add(Shapes.Cylinder(outer + 0.06f, outer + 0.06f, 0.3f, Segments * 2), Matrix4x4.CreateTranslation(0f, UpperFloor - 0.2f, 0f));
        b.Roof.Add(Shapes.Cone(outer + 0.55f, 3.4f, Segments * 2), Matrix4x4.CreateTranslation(0f, WallTop + 0.2f + 1.7f, 0f));
        b.Accent.Add(Shapes.Sphere(0.18f, 12, 6), Matrix4x4.CreateTranslation(0f, WallTop + 0.2f + 3.45f, 0f));

        // A ladder to the upper floor, a table and a chest.
        b.Wood.Span(new Vector3(-0.9f, FloorTop, -1.55f), new Vector3(-0.85f, UpperFloor, -1.45f), Matrix4x4.Identity);
        b.Wood.Span(new Vector3(-0.35f, FloorTop, -1.55f), new Vector3(-0.3f, UpperFloor, -1.45f), Matrix4x4.Identity);
        for (float y = FloorTop + 0.3f; y < UpperFloor - 0.1f; y += 0.35f)
        {
            b.Wood.Span(new Vector3(-0.85f, y, -1.53f), new Vector3(-0.35f, y + 0.04f, -1.47f), Matrix4x4.Identity);
        }

        b.Wood.Add(Shapes.Cylinder(0.5f, 0.5f, 0.06f, 16), Matrix4x4.CreateTranslation(0.6f, FloorTop + 0.75f, 0.2f));
        b.Wood.Add(Shapes.Cylinder(0.08f, 0.12f, 0.72f, 8), Matrix4x4.CreateTranslation(0.6f, FloorTop + 0.36f, 0.2f));
        b.Accent.Span(new Vector3(0.6f, FloorTop + 0.02f, -1.2f), new Vector3(1.4f, FloorTop + 0.55f, -0.7f), Matrix4x4.Identity);
        b.Wood.Span(new Vector3(-1.2f, UpperFloor, 0.3f), new Vector3(0.6f, UpperFloor + 0.5f, 1.2f), Matrix4x4.Identity);
        b.Accent.Span(new Vector3(-1.15f, UpperFloor + 0.5f, 0.35f), new Vector3(0.55f, UpperFloor + 0.62f, 1.15f), Matrix4x4.Identity);

        var lamp = new Vector3(4.4f, 2.7f, 1.3f);
        b.HangingLamp(new Vector3(0.3f, UpperFloor - 0.8f, 0.4f), UpperFloor - 0.18f, lamp, 7f);
        b.HangingLamp(new Vector3(-0.2f, WallTop - 1.1f, -0.2f), WallTop, lamp, 8f);

        return b.Build(
            content,
            "tower",
            new Vector3(-outer - 0.6f, -1.4f, -outer - 0.6f),
            new Vector3(outer + 0.6f, WallTop + 3.8f, outer + 0.6f),
            new Vector3(0f, 0f, outer + 1.5f),
            new Vector3(0f, FloorTop, inner - 0.5f));
    }
}
