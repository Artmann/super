using System.Numerics;
using Rendition;
using Super;

namespace Hamlet;

/// <summary>
/// A smithy: one tall room with a wide open doorway under a gable, a forge in the back
/// corner whose fire burns all day, an anvil, a workbench and a lamp over the middle.
/// </summary>
internal static class Workshop
{
    private const float HalfWidth = 3.2f;
    private const float HalfDepth = 2.4f;
    private const float Thickness = 0.3f;
    private const float FloorTop = 0.1f;
    private const float WallTop = 3.3f;
    private const float Rise = 1.8f;
    private const float DoorAlong = -0.6f;
    private const float DoorWidth = 3.0f;

    public static BuildingPlan Build(Content content, Palette palette)
    {
        var b = new PlanBuilder(palette);
        float outerX = HalfWidth + Thickness;
        float outerZ = HalfDepth + Thickness;
        float wallMiddleX = HalfWidth + Thickness * 0.5f;
        float wallMiddleZ = HalfDepth + Thickness * 0.5f;

        b.Stone.Span(new Vector3(-outerX - 0.12f, -1.5f, -outerZ - 0.12f), new Vector3(outerX + 0.12f, FloorTop, outerZ + 0.12f), Matrix4x4.Identity);
        // An apron of stone in front of the big doorway.
        b.Stone.Span(new Vector3(DoorAlong - DoorWidth * 0.5f - 0.4f, -0.8f, outerZ), new Vector3(DoorAlong + DoorWidth * 0.5f + 0.4f, FloorTop - 0.02f, outerZ + 1.6f), Matrix4x4.Identity);

        b.Wall(PlanBuilder.Facing(new Vector3(0f, 0f, wallMiddleZ), Vector3.UnitZ), outerX * 2f, Thickness, FloorTop, WallTop,
            new Opening(DoorAlong, DoorWidth, FloorTop, 2.8f, Window: false));
        b.Wall(PlanBuilder.Facing(new Vector3(0f, 0f, -wallMiddleZ), -Vector3.UnitZ), outerX * 2f, Thickness, FloorTop, WallTop,
            new Opening(1.2f, 0.9f, 1.3f, 2.3f));
        b.Wall(PlanBuilder.Facing(new Vector3(-wallMiddleX, 0f, 0f), -Vector3.UnitX), HalfDepth * 2f, Thickness, FloorTop, WallTop,
            new Opening(0.3f, 0.9f, 1.3f, 2.3f));
        b.Wall(PlanBuilder.Facing(new Vector3(wallMiddleX, 0f, 0f), Vector3.UnitX), HalfDepth * 2f, Thickness, FloorTop, WallTop,
            new Opening(0.6f, 0.9f, 1.3f, 2.3f));

        // The ridge runs front to back, so the doorway sits under a gable end.
        var roofFrame = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, WallTop, 0f);
        b.Plaster.Gable(outerX, Rise, outerZ * 2f, roofFrame);
        b.Roof.PitchedRoof(outerX, Rise, outerZ * 2f, 0.45f, 0.14f, roofFrame);
        b.Wood.Box(new Vector3(0.16f, 0.16f, outerZ * 2f + 1f), new Vector3(0f, WallTop + Rise + 0.12f, 0f));
        // Tie beams across, under the open roof.
        for (int i = -1; i <= 1; i++)
        {
            b.Wood.Box(new Vector3(HalfWidth * 2f, 0.18f, 0.18f), new Vector3(0f, WallTop - 0.1f, i * 1.5f));
        }

        b.Floor.Span(new Vector3(-HalfWidth, FloorTop, -HalfDepth), new Vector3(HalfWidth, FloorTop + 0.02f, HalfDepth), Matrix4x4.Identity);

        // The forge in the back right corner, with its hood and chimney through the roof.
        var forge = new Vector3(2.3f, FloorTop, -1.6f);
        b.Stone.Span(forge + new Vector3(-0.8f, 0f, -0.8f), forge + new Vector3(0.9f, 0.85f, 0.7f), Matrix4x4.Identity);
        b.Embers.Span(forge + new Vector3(-0.45f, 0.85f, -0.45f), forge + new Vector3(0.55f, 0.92f, 0.35f), Matrix4x4.Identity);
        b.Stone.Span(forge + new Vector3(-0.7f, 2.0f, -0.8f), forge + new Vector3(0.9f, 2.4f, 0.5f), Matrix4x4.Identity);
        b.Stone.Span(forge + new Vector3(-0.2f, 2.4f, -0.6f), forge + new Vector3(0.5f, WallTop + Rise + 0.6f, 0.1f), Matrix4x4.Identity);

        // The anvil on its block, a workbench along the left wall and a water trough.
        b.Wood.Add(Shapes.Cylinder(0.3f, 0.32f, 0.55f, 14), Matrix4x4.CreateTranslation(0.6f, FloorTop + 0.275f, -0.6f));
        b.Iron.Span(new Vector3(0.25f, FloorTop + 0.55f, -0.75f), new Vector3(0.95f, FloorTop + 0.8f, -0.45f), Matrix4x4.Identity);
        b.Wood.Span(new Vector3(-HalfWidth, FloorTop + 0.85f, -1.8f), new Vector3(-HalfWidth + 0.8f, FloorTop + 0.92f, 1.2f), Matrix4x4.Identity);
        foreach (float z in (ReadOnlySpan<float>)[-1.7f, 1.1f])
        {
            b.Wood.Span(new Vector3(-HalfWidth + 0.05f, FloorTop, z - 0.05f), new Vector3(-HalfWidth + 0.75f, FloorTop + 0.85f, z + 0.05f), Matrix4x4.Identity);
        }

        b.Accent.Span(new Vector3(-HalfWidth + 0.15f, FloorTop + 0.92f, -1.2f), new Vector3(-HalfWidth + 0.55f, FloorTop + 1.2f, -0.6f), Matrix4x4.Identity);
        b.Wood.Span(new Vector3(1.2f, FloorTop, 0.6f), new Vector3(2.6f, FloorTop + 0.5f, 1.2f), Matrix4x4.Identity);
        b.Water.Span(new Vector3(1.28f, FloorTop + 0.42f, 0.68f), new Vector3(2.52f, FloorTop + 0.48f, 1.12f), Matrix4x4.Identity);

        // The doors, folded back flat against the front wall outside, in the strong colour.
        float left = DoorAlong - DoorWidth * 0.5f;
        float right = DoorAlong + DoorWidth * 0.5f;
        b.Accent.Span(new Vector3(-outerX + 0.05f, FloorTop + 0.05f, outerZ), new Vector3(left - 0.1f, 2.7f, outerZ + 0.07f), Matrix4x4.Identity);
        b.Accent.Span(new Vector3(right + 0.1f, FloorTop + 0.05f, outerZ), new Vector3(right + 1.5f, 2.7f, outerZ + 0.07f), Matrix4x4.Identity);

        b.HangingLamp(new Vector3(-0.6f, 2.7f, 0.2f), WallTop - 0.2f, new Vector3(4.6f, 2.8f, 1.3f), 8f);
        // The forge's glow, thrown out across the room towards the doorway. It burns all day, so it is mixed:
        // the bake holds its glow bounced round the room, and its direct light and shadows stay live.
        b.Lamp(new Lamp(
            forge + new Vector3(0f, 1.1f, 0.2f),
            Vector3.Normalize(new Vector3(-0.45f, -0.35f, 1f)),
            new Vector3(5f, 1.6f, 0.35f),
            9f,
            0.5f,
            1.3f,
            Fire: true,
            Mode: LightMode.Mixed));

        return b.Build(
            content,
            "workshop",
            new Vector3(-outerX - 0.5f, -1.5f, -outerZ - 0.5f),
            new Vector3(outerX + 0.5f, WallTop + Rise + 0.7f, outerZ + 1.6f),
            new Vector3(DoorAlong, 0f, outerZ + 2.5f),
            new Vector3(DoorAlong, FloorTop, HalfDepth - 0.5f));
    }
}
