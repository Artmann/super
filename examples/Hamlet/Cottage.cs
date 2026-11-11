using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>A cottage's walls, for <see cref="CottageLayout"/>.</summary>
internal enum CottageWall
{
    Left,
    Right,
    Back,
}

/// <summary>Which wall each piece of furniture stands against. Each piece has a wall of its own.</summary>
internal readonly record struct CottageLayout(CottageWall Bed, CottageWall Table, CottageWall Shelf)
{
    /// <summary>Every way to stand the three pieces against the three walls.</summary>
    public static readonly CottageLayout[] All =
    [
        new(CottageWall.Left, CottageWall.Right, CottageWall.Back),
        new(CottageWall.Left, CottageWall.Back, CottageWall.Right),
        new(CottageWall.Right, CottageWall.Left, CottageWall.Back),
        new(CottageWall.Right, CottageWall.Back, CottageWall.Left),
        new(CottageWall.Back, CottageWall.Left, CottageWall.Right),
        new(CottageWall.Back, CottageWall.Right, CottageWall.Left),
    ];
}

/// <summary>
/// A one-room cottage with a pitched roof and a walled front garden. A window in each side
/// wall, one in the back and one beside the door let the sun in by day. Inside, a bed, a
/// table and a shelf stand against the walls as the layout says, the back wall is painted
/// the building's strong colour, and a lamp hangs from the ceiling.
/// </summary>
internal static class Cottage
{
    private const float HalfWidth = 2.5f;
    private const float HalfDepth = 2.0f;
    private const float Thickness = 0.3f;
    private const float FloorTop = 0.12f;
    private const float WallTop = 2.75f;
    private const float CeilingTop = 2.9f;
    private const float Rise = 1.7f;
    private const float DoorAlong = -0.8f;
    private const float GardenDepth = 3.6f;
    private const float GardenHalfWidth = 3.6f;

    public static BuildingPlan Build(Content content, Palette palette, CottageLayout layout)
    {
        var b = new PlanBuilder(palette);
        float outerX = HalfWidth + Thickness;
        float outerZ = HalfDepth + Thickness;
        float wallMiddleX = HalfWidth + Thickness * 0.5f;
        float wallMiddleZ = HalfDepth + Thickness * 0.5f;

        // A stone footing that reaches down into the slope, and a wooden floor.
        b.Stone.Span(new Vector3(-outerX - 0.12f, -1.5f, -outerZ - 0.12f), new Vector3(outerX + 0.12f, FloorTop, outerZ + 0.12f), Matrix4x4.Identity);
        b.Floor.Span(new Vector3(-HalfWidth, FloorTop, -HalfDepth), new Vector3(HalfWidth, FloorTop + 0.03f, HalfDepth), Matrix4x4.Identity);

        // The front and back walls run the full width; the side walls fit between them.
        // Openings are measured along each wall's own x, which runs the other way on the
        // back wall and on the right.
        b.Wall(PlanBuilder.Facing(new Vector3(0f, 0f, wallMiddleZ), Vector3.UnitZ), outerX * 2f, Thickness, FloorTop, WallTop,
            new Opening(DoorAlong, 1.0f, FloorTop, 2.05f, Window: false),
            new Opening(1.3f, 0.9f, 1.0f, 1.95f));
        b.Wall(PlanBuilder.Facing(new Vector3(0f, 0f, -wallMiddleZ), -Vector3.UnitZ), outerX * 2f, Thickness, FloorTop, WallTop,
            new Opening(-1.4f, 0.9f, 1.0f, 1.95f));
        b.Wall(PlanBuilder.Facing(new Vector3(-wallMiddleX, 0f, 0f), -Vector3.UnitX), HalfDepth * 2f, Thickness, FloorTop, WallTop,
            new Opening(0.9f, 0.9f, 1.0f, 1.95f));
        b.Wall(PlanBuilder.Facing(new Vector3(wallMiddleX, 0f, 0f), Vector3.UnitX), HalfDepth * 2f, Thickness, FloorTop, WallTop,
            new Opening(-0.9f, 0.9f, 1.0f, 1.95f));

        // A flat ceiling, the gable ends above it, and the roof.
        b.Plaster.Span(new Vector3(-outerX, WallTop, -outerZ), new Vector3(outerX, CeilingTop, outerZ), Matrix4x4.Identity);
        b.Plaster.Gable(outerZ, Rise, outerX * 2f, Matrix4x4.CreateTranslation(0f, CeilingTop, 0f));
        b.Roof.PitchedRoof(outerZ, Rise, outerX * 2f, 0.4f, 0.14f, Matrix4x4.CreateTranslation(0f, CeilingTop, 0f));
        b.Wood.Box(new Vector3(outerX * 2f + 0.9f, 0.16f, 0.16f), new Vector3(0f, CeilingTop + Rise + 0.12f, 0f));

        // The back wall's inside, painted, beside its window.
        b.Accent.Span(new Vector3(-HalfWidth, FloorTop, -HalfDepth), new Vector3(0.95f, WallTop, -HalfDepth + 0.015f), Matrix4x4.Identity);
        b.Accent.Span(new Vector3(1.85f, FloorTop, -HalfDepth), new Vector3(HalfWidth, WallTop, -HalfDepth + 0.015f), Matrix4x4.Identity);

        // The door stands open, folded back flat against the inside of the front wall.
        float hinge = DoorAlong - 0.5f;
        b.Accent.Span(new Vector3(hinge - 0.95f, FloorTop + 0.02f, HalfDepth - 0.06f), new Vector3(hinge, 2.02f, HalfDepth), Matrix4x4.Identity);

        Furniture(b, layout.Bed, Bed);
        Furniture(b, layout.Table, Table);
        Furniture(b, layout.Shelf, Shelf);

        b.HangingLamp(new Vector3(0.2f, 2.3f, -0.3f), WallTop, new Vector3(4.2f, 2.6f, 1.25f), 7f);

        // The garden: a low wall round the front with a gate in line with the door.
        float wallHeight = 0.85f;
        float front = outerZ + GardenDepth;
        float gate = 1.2f;
        b.Stone.Span(new Vector3(-GardenHalfWidth, -0.8f, outerZ - 0.1f), new Vector3(-GardenHalfWidth + 0.3f, wallHeight, front), Matrix4x4.Identity);
        b.Stone.Span(new Vector3(GardenHalfWidth - 0.3f, -0.8f, outerZ - 0.1f), new Vector3(GardenHalfWidth, wallHeight, front), Matrix4x4.Identity);
        b.Stone.Span(new Vector3(-GardenHalfWidth, -0.8f, front - 0.3f), new Vector3(DoorAlong - gate * 0.5f, wallHeight, front), Matrix4x4.Identity);
        b.Stone.Span(new Vector3(DoorAlong + gate * 0.5f, -0.8f, front - 0.3f), new Vector3(GardenHalfWidth, wallHeight, front), Matrix4x4.Identity);
        b.Floor.Span(new Vector3(DoorAlong - 0.45f, -0.3f, outerZ), new Vector3(DoorAlong + 0.45f, 0.06f, front), Matrix4x4.Identity);

        return b.Build(
            content,
            "cottage",
            new Vector3(-GardenHalfWidth, -1.5f, -outerZ - 0.4f),
            new Vector3(GardenHalfWidth, CeilingTop + Rise + 0.3f, front),
            new Vector3(DoorAlong, 0f, front + 1f),
            new Vector3(DoorAlong, FloorTop, HalfDepth - 0.4f));
    }

    /// <summary>
    /// Stands a piece of furniture against a wall. Each piece is made with its back on
    /// z = 0 and its middle on x = 0, facing +z into the room.
    /// </summary>
    private static void Furniture(PlanBuilder b, CottageWall wall, Action<PlanBuilder, Matrix4x4> piece)
    {
        var frame = wall switch
        {
            CottageWall.Left => Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-HalfWidth, FloorTop, -0.95f),
            CottageWall.Right => Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(HalfWidth, FloorTop, -0.95f),
            _ => Matrix4x4.CreateTranslation(-0.2f, FloorTop, -HalfDepth),
        };
        piece(b, frame);
    }

    private static void Bed(PlanBuilder b, Matrix4x4 frame)
    {
        b.Wood.Span(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0.42f, 0.9f), frame);
        b.Wood.Span(new Vector3(-1f, 0f, 0f), new Vector3(-0.92f, 0.85f, 0.9f), frame);
        b.Accent.Span(new Vector3(-0.55f, 0.42f, -0.01f), new Vector3(1.02f, 0.56f, 0.92f), frame);
        b.Plaster.Span(new Vector3(-0.9f, 0.42f, 0.12f), new Vector3(-0.58f, 0.55f, 0.78f), frame);
    }

    private static void Table(PlanBuilder b, Matrix4x4 frame)
    {
        b.Wood.Span(new Vector3(-0.6f, 0.72f, 0.05f), new Vector3(0.6f, 0.78f, 0.85f), frame);
        foreach (var (x, z) in (ReadOnlySpan<(float, float)>)[(-0.52f, 0.12f), (0.52f, 0.12f), (-0.52f, 0.78f), (0.52f, 0.78f)])
        {
            b.Wood.Span(new Vector3(x - 0.04f, 0f, z - 0.04f), new Vector3(x + 0.04f, 0.72f, z + 0.04f), frame);
        }

        // A stool, and a jug and a bowl on the table.
        b.Wood.Span(new Vector3(-0.2f, 0f, 1.0f), new Vector3(0.2f, 0.45f, 1.35f), frame);
        b.Accent.Add(Shapes.Cylinder(0.07f, 0.09f, 0.22f, 12), Matrix4x4.CreateTranslation(0.25f, 0.89f, 0.4f) * frame);
        b.Stone.Add(Shapes.Cylinder(0.14f, 0.09f, 0.07f, 12), Matrix4x4.CreateTranslation(-0.2f, 0.815f, 0.45f) * frame);
    }

    private static void Shelf(PlanBuilder b, Matrix4x4 frame)
    {
        b.Wood.Span(new Vector3(-0.55f, 0f, 0f), new Vector3(-0.5f, 1.8f, 0.35f), frame);
        b.Wood.Span(new Vector3(0.5f, 0f, 0f), new Vector3(0.55f, 1.8f, 0.35f), frame);
        for (int i = 0; i < 4; i++)
        {
            float y = 0.1f + i * 0.55f;
            b.Wood.Span(new Vector3(-0.5f, y, 0f), new Vector3(0.5f, y + 0.04f, 0.35f), frame);
        }

        // A few pots and a box on the shelves.
        b.Accent.Span(new Vector3(-0.4f, 0.69f, 0.05f), new Vector3(-0.1f, 0.89f, 0.3f), frame);
        b.Stone.Add(Shapes.Cylinder(0.08f, 0.08f, 0.2f, 10), Matrix4x4.CreateTranslation(0.25f, 0.79f, 0.17f) * frame);
        b.Stone.Add(Shapes.Cylinder(0.1f, 0.07f, 0.16f, 10), Matrix4x4.CreateTranslation(-0.2f, 1.32f, 0.17f) * frame);
    }
}
