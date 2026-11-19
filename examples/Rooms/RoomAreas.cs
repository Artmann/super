using System.Numerics;
using Rendition;
using Super;

namespace Rooms;

/// <summary>The two areas the player walks between, the house and outside, each built as a scene as the player comes in.</summary>
internal sealed class RoomAreas(Content content, PlayerMover player, CoinHunt coins)
{
    public const float HouseSize = 5f;
    public const float OutsideSize = 12f;

    public static readonly Vector3 HouseDoor = new(0f, 0f, HouseSize);
    public static readonly Vector3 OutsideDoor = new(0f, 0f, -OutsideSize);
    public static readonly Vector3 DoorColor = new(0.2f, 0.5f, 0.8f);

    private const float ArrivalGap = 2f;
    private const int GroundTextureSize = 512;

    private static readonly Vector3 FloorColor = new(0.45f, 0.33f, 0.22f);

    private ModelRef? _grass;

    /// <summary>Builds the grass outside while the loading screen shows, as it takes a moment.</summary>
    public void Load()
    {
        _grass ??= content.Mesh(Grass());
    }

    /// <summary>The house: a wooden floor and a door out. Coming from outside means coming through the door.</summary>
    public void House(Scene scene, Room? from)
    {
        player.Camera.Offset = new Vector3(0f, 10f, 10f);
        Light(scene);
        scene.Spawn(Flat(HouseSize), new Model(ModelRef.Cube, FloorColor));
        Door(scene, HouseDoor);
        player.Move(from == Room.Outside ? HouseDoor - new Vector3(0f, 0f, ArrivalGap) : Vector3.Zero);
    }

    /// <summary>Outside: grass, a door back in, and coins to pick up. The player comes in through the door.</summary>
    public void Outside(Scene scene)
    {
        player.Camera.Offset = new Vector3(0f, 14f, 14f);
        Light(scene);
        scene.Spawn(Flat(OutsideSize), new Model(_grass ?? ModelRef.Cube));
        Door(scene, OutsideDoor);
        coins.Scatter(scene, OutsideSize);
        player.Move(OutsideDoor + new Vector3(0f, 0f, ArrivalGap));
    }

    private static void Light(Scene scene) => scene.Spawn(new Sun(new Vector3(1f, 2f, 1f), new Vector3(1.4f)));

    /// <summary>A door, and a lid over it that lifts as the player comes close.</summary>
    private static void Door(Scene scene, Vector3 spot)
    {
        scene.Spawn(Transform.At(spot), new Model(ModelRef.Cube, DoorColor));
        var lid = new Transform(spot + new Vector3(0f, 0.6f, 0f), Quaternion.Identity, new Vector3(1.2f, 0.2f, 1.2f));
        scene.Spawn(lid, new Model(ModelRef.Cube, DoorColor), new DoorLid(spot, Lift: 0f));
    }

    private static Transform Flat(float size) =>
        new(new Vector3(0f, -0.6f, 0f), Quaternion.Identity, new Vector3(size * 2f, 0.2f, size * 2f));

    /// <summary>A checkered square, large enough that building it is worth doing in the background.</summary>
    private static GeneratedMesh Grass()
    {
        byte[] pixels = new byte[GroundTextureSize * GroundTextureSize * 4];
        for (int y = 0; y < GroundTextureSize; y++)
        {
            for (int x = 0; x < GroundTextureSize; x++)
            {
                bool light = (x / 32 + y / 32) % 2 == 0;
                int pixel = (y * GroundTextureSize + x) * 4;
                pixels[pixel] = (byte)(light ? 90 : 70);
                pixels[pixel + 1] = (byte)(light ? 150 : 125);
                pixels[pixel + 2] = 60;
                pixels[pixel + 3] = 255;
            }
        }

        var up = Vector3.UnitY;
        var along = new Vector4(1f, 0f, 0f, 1f);
        Vertex[] vertices =
        [
            new(new Vector3(-0.5f, 0.5f, -0.5f), up, along, new Vector2(0f, 0f)),
            new(new Vector3(0.5f, 0.5f, -0.5f), up, along, new Vector2(1f, 0f)),
            new(new Vector3(0.5f, 0.5f, 0.5f), up, along, new Vector2(1f, 1f)),
            new(new Vector3(-0.5f, 0.5f, 0.5f), up, along, new Vector2(0f, 1f)),
        ];
        return new GeneratedMesh(vertices, [0, 2, 1, 0, 3, 2], new GeneratedTexture(GroundTextureSize, pixels), 0.9f);
    }
}
