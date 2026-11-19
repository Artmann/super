using System.Numerics;
using Super;
using Hole = Gallery.GalleryShapes.Hole;

namespace Gallery;

/// <summary>
/// The gallery's walls, floors, roofs and furniture: plain boxes and cylinders in flat matte
/// materials, built once in Load. North is -z. The corridor runs north from the entrance hall,
/// with the rooms off its west side and the colonnade on its east side, and ends at the
/// courtyard behind a glass screen.
/// </summary>
public sealed class Building(World world, Content content, Dictionary<string, Surface> materials)
{
    private const float Wall = GalleryShapes.Wall;

    private readonly GalleryShapes _shapes = new(world, content, materials);

    public void Build()
    {
        BuildFloors();
        BuildHall();
        BuildCorridor();
        BuildRoomWalls();
        BuildColourAndWhaleRooms();
        BuildLampRoom();
        BuildChapel();
        BuildCourtyard();
    }

    private void BuildFloors()
    {
        // One floor under everything, then lighter or darker floors laid over it.
        _shapes.Box(new(-11.6f, -0.3f, -56.6f), new(7.6f, 0f, 10.6f), "floor");
        _shapes.Box(new(-11f, 0f, -10f + Wall / 2f), new(-2.3f, 0.01f, -Wall), "white");
        _shapes.Box(new(2.6f, 0f, -42f), new(7f, 0.01f, -Wall), "gravel");
        _shapes.Box(new(-11.3f, 0f, -56f), new(7.3f, 0.01f, -42.3f), "flags");
    }

    private void BuildHall()
    {
        // The entrance hall, 12 m wide and 6 m tall, with a door in the south wall and the
        // corridor leaving from the middle of its north wall.
        _shapes.WallAlongX(10f, 10f + Wall, -6.3f, 6.3f, 6f, new Hole(-1.2f, 1.2f, 0f, 3f));
        _shapes.WallAlongZ(-6.3f, -6f, -Wall, 10f, 6f);
        _shapes.WallAlongZ(6f, 6.3f, -Wall, 10f, 6f);

        // Its north wall runs on west as the colour room's south wall, so it reaches that room's height.
        _shapes.WallAlongX(-Wall, 0f, -11.3f, 6.3f, 7f, new Hole(-2f, 2.6f, 0f, 4.5f));
        // East of the hall it's only the garden's low wall.
        _shapes.WallAlongX(-Wall, 0f, 6.3f, 7.3f, 1.2f);

        // The roof, with a long skylight off to the east, so the sun's stripe lands in the middle of the floor.
        var skylight = GalleryPlan.HallSkylight;
        _shapes.Roof(-6.3f, 6.3f, -Wall, 10f + Wall, 6f, new Hole(skylight.Min.X, skylight.Max.X, skylight.Min.Z, skylight.Max.Z));

        // Two benches facing each other across the stripe.
        _shapes.Box(new(-4.6f, 0f, 2.5f), new(-4.1f, 0.45f, 7.5f), "wood");
        _shapes.Box(new(3.3f, 0f, 2.5f), new(3.8f, 0.45f, 7.5f), "wood");
    }

    private void BuildCorridor()
    {
        // The corridor roof covers the walkway and the colonnade, and stops at the glass screen.
        _shapes.Roof(-2.3f, 2.6f, -42f, -Wall, 4.5f);

        // Columns every 2.5 m along the east side, under a beam, open to the garden.
        _shapes.Box(new(1.95f, 4.15f, -42f), new(2.55f, 4.5f, -Wall), "plaster");
        for (float z = -1.5f; z > -42f; z -= 2.5f)
        {
            _shapes.Cylinder(new Vector3(2.25f, 0f, z), 0.2f, 4.15f, "plaster");
            _shapes.Box(new(2.0f, 0f, z - 0.25f), new(2.5f, 0.25f, z + 0.25f), "plaster");
        }

        // The garden's low walls, on the far side from the colonnade and at its north end.
        _shapes.WallAlongZ(7f, 7.3f, -42.3f, 0f, 1.2f);
        _shapes.WallAlongX(-42.3f, -42f, 2.6f, 7f, 1.2f);

        // The tinted glass screen across the end of the corridor, in a frame.
        const float ScreenZ = GalleryPlan.ScreenZ;
        _shapes.Box(new(-2f, 0f, ScreenZ - 0.02f), new(2.6f, 4.5f, ScreenZ + 0.02f), "glass");
        foreach (float x in new[] { -2f, -0.45f, 1.1f, 2.6f })
        {
            _shapes.Box(new(x - 0.06f, 0f, ScreenZ - 0.06f), new(x + 0.06f, 4.5f, ScreenZ + 0.06f), "frame");
        }

        _shapes.Box(new(-2f, 2.2f, ScreenZ - 0.05f), new(2.6f, 2.3f, ScreenZ + 0.05f), "frame");
    }

    private void BuildRoomWalls()
    {
        // The rooms' east walls make the corridor's west wall. Each room has a door onto the
        // corridor; the colour room also has a clerestory window above the corridor roof.
        var window = GalleryPlan.ColourWindow;
        _shapes.WallAlongZ(-2.3f, -2f, -10f, -Wall, 7f,
            new Hole(-5.8f, -3.8f, 0f, 3f),
            new Hole(window.Min.Z, window.Max.Z, window.Min.Y, window.Max.Y));
        _shapes.WallAlongZ(-2.3f, -2f, -20f, -10f, 7f, new Hole(-15.7f, -13.7f, 0f, 3f));
        _shapes.WallAlongZ(-2.3f, -2f, -30f, -20f, 4.5f, new Hole(-26f, -24f, 0f, 3f));
        _shapes.WallAlongZ(-2.3f, -2f, -42f, -30f, 8f, new Hole(-37f, -35f, 0f, 3f));

        // The west wall, a little taller where each room is taller.
        _shapes.WallAlongZ(-11.3f, -11f, -10f, -Wall, 7f);
        _shapes.WallAlongZ(-11.3f, -11f, -20f, -10f, 7f);
        _shapes.WallAlongZ(-11.3f, -11f, -30f, -20f, 4.5f);
        _shapes.WallAlongZ(-11.3f, -11f, -42.3f, -30f, 8f);

        // Walls between the rooms, as tall as the taller room either side.
        _shapes.WallAlongX(-10f - Wall / 2f, -10f + Wall / 2f, -11f, -2.3f, 7f);
        _shapes.WallAlongX(-20f - Wall / 2f, -20f + Wall / 2f, -11f, -2.3f, 7f);
        _shapes.WallAlongX(-30f - Wall / 2f, -30f + Wall / 2f, -11f, -2.3f, 8f);
        _shapes.WallAlongX(-42.3f, -42f, -11.3f, -2f, 8f);
    }

    private void BuildColourAndWhaleRooms()
    {
        // The colour room: a red wall to the north and a blue one to the south, laid over the plaster.
        _shapes.Box(new(-11f, 0.01f, -10f + Wall / 2f), new(-2.3f, 7f, -10f + Wall / 2f + 0.03f), "red");
        _shapes.Box(new(-11f, 0.01f, -Wall - 0.03f), new(-2.3f, 7f, -Wall), "blue");
        _shapes.Roof(-11.3f, -2f, -10f, -Wall, 7f);
        var bust = GalleryPlan.BustAt;
        _shapes.Box(new(bust.X - 0.2f, 0f, bust.Z - 0.2f), new(bust.X + 0.2f, bust.Y, bust.Z + 0.2f), "plaster");
        _shapes.Box(new(bust.X - 0.26f, 0f, bust.Z - 0.26f), new(bust.X + 0.26f, 0.12f, bust.Z + 0.26f), "plaster");

        // The whale room, with a skylight placed so the sun lands on the whale.
        var skylight = GalleryPlan.WhaleSkylight;
        _shapes.Roof(-11.3f, -2f, -20f, -10f, 7f, new Hole(skylight.Min.X, skylight.Max.X, skylight.Min.Z, skylight.Max.Z));
        var whale = GalleryPlan.WhaleAt;
        _shapes.Box(new(whale.X - 0.8f, 0f, whale.Z - 1.1f), new(whale.X + 0.8f, whale.Y, whale.Z + 1.1f), "plaster");
    }

    private void BuildLampRoom()
    {
        // The lamp room is shut but for its door. A strip of glowing panels runs along the floor,
        // and three lamps hang from the roof: a dark shade over a glowing disc.
        _shapes.Roof(-11.3f, -2f, -30f, -20f, 4.5f);
        for (int i = 0; i < 10; i++)
        {
            float x = -10.4f + i * 0.75f;
            _shapes.Box(new(x, 0f, -28.2f), new(x + 0.6f, 0.03f, -27.6f), "panel");
        }

        foreach (var lamp in GalleryPlan.Lamps)
        {
            _shapes.Cylinder(lamp + new Vector3(0f, 0.31f, 0f), 0.008f, 4.5f - lamp.Y - 0.31f, "shade");
            _shapes.Cone(lamp + new Vector3(0f, 0.01f, 0f), 0.3f, 0.06f, 0.3f, "shade");
            _shapes.Cylinder(lamp - new Vector3(0f, 0.01f, 0f), 0.27f, 0.02f, "lamp");
        }
    }

    private void BuildChapel()
    {
        // The chapel: tall, with an altar at the west end and benches either side of the aisle.
        _shapes.Roof(-11.3f, -2f, -42.3f, -30f, 8f);
        _shapes.Box(new(-10.7f, 0f, -37.6f), new(-9.6f, 1f, -34.4f), "plaster");
        _shapes.Box(new(-10.9f, 1f, -37.8f), new(-9.4f, 1.08f, -34.2f), "plaster");
        for (int row = 0; row < 4; row++)
        {
            float x = -8.4f + row * 1.3f;
            foreach (var (from, to) in new[] { (-40.8f, -37.8f), (-34.2f, -31.2f) })
            {
                _shapes.Box(new(x - 0.2f, 0.4f, from), new(x + 0.2f, 0.47f, to), "wood");
                _shapes.Box(new(x + 0.15f, 0.47f, from), new(x + 0.2f, 0.95f, to), "wood");
                _shapes.Box(new(x - 0.15f, 0f, from + 0.1f), new(x + 0.15f, 0.4f, from + 0.2f), "wood");
                _shapes.Box(new(x - 0.15f, 0f, to - 0.2f), new(x + 0.15f, 0.4f, to - 0.1f), "wood");
            }
        }
    }

    private void BuildCourtyard()
    {
        // Open to the sky, walled on three sides, with a basin in the middle.
        _shapes.WallAlongZ(-11.6f, -11.3f, -56.3f, -42.3f, 4f);
        _shapes.WallAlongZ(7.3f, 7.6f, -56.3f, -42.3f, 4f);
        _shapes.WallAlongX(-56.3f, -56f, -11.6f, 7.6f, 4f);

        _shapes.Cylinder(new Vector3(-1f, 0f, -49.5f), 1.8f, 0.55f, "plaster");
        _shapes.Cylinder(new Vector3(-1f, 0.55f, -49.5f), 1.55f, 0.01f, "water");
        _shapes.Cylinder(new Vector3(-1f, 0f, -49.5f), 0.25f, 1.6f, "plaster");

        _shapes.Box(new(3.2f, 0f, -53.4f), new(4.2f, 1f, -52.4f), "plaster");
        _shapes.Sphere(new Vector3(3.7f, 1.5f, -52.9f), 0.5f, "orange");
        _shapes.Box(new(-8.8f, 0f, -53.5f), new(-6.8f, 0.45f, -52.9f), "wood");
        _shapes.Cylinder(new Vector3(-8f, 0f, -46f), 0.18f, 2.2f, "wood");
        _shapes.Sphere(new Vector3(-8f, 2.9f, -46f), 1.1f, "leaves");
    }
}
