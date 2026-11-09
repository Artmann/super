using System.Numerics;
using Rendition;
using Super;

namespace Courtyard;

/// <summary>
/// A sunlit courtyard modelled in Blender (art/courtyard.py): a brick wall with an arched
/// window, a dry-stone wall, a flagstone path through trodden mud and a puddle. Its bricks, stone,
/// mud, wood, rust and window glass are the material graphs, put on the model by its cook sidecar.
/// M turns the materials off and on, to judge the lighting and the shapes on their own, F the fog and D the grime decals.
/// </summary>
public sealed class CourtyardGame(Content content, Input input, Graphics graphics) : IGame
{
    // Light keys, apart from the model's key of 0.
    private const int LampKey = 1;
    private const int RoomKey = 2;
    private const int YardProbeKey = 1;

    // The model keeps Blender's origin (keepOrigin in its sidecar), so a point from
    // art/courtyard.py is the same here, with Blender's z as y and its y as -z.
    // The middle of the bulb in the wall lantern (build_lamp), 0.31 m out from the wall.
    private static readonly Vector3 LampBulb = new(4.7f, 3.95f, -3.688f);

    // A lamp hung near the back of the room behind the window (build_window), a little to the
    // left, so its walls fall off from warm to dark.
    private static readonly Vector3 RoomLamp = new(1.6f, 3.9f, -7.4f);

    // From art/courtyard.py: the wall's face, its pier and the window sill, in Blender's metres.
    private const float WallY = 4.0f;
    private const float WallX0 = -2.8f;
    private const float WallX1 = 11.0f;
    private const float PierX0 = 6.3f;
    private const float PierX1 = 7.0f;
    private const float PierDepth = 0.14f;
    private const float SillZ = 1.15f;
    private const float WindowX = 2.4f;
    private const float WindowWidth = 2.0f;

    private ModelRef _courtyard;
    private MeshMask? _withoutStains;
    private readonly List<(Matrix4x4 Box, Surface Surface)> _stains = [];

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        // The courtyard in plain clay, through the same key a player presses.
        ["plain"] = new InputScript().Press(Key.M).Wait(1),
        // The courtyard in clear air, to compare with the fog.
        ["clear"] = new InputScript().Press(Key.F).Wait(1),
        // The courtyard at a fixed exposure, to compare with what the camera meters.
        ["fixed"] = new InputScript().Press(Key.E).Wait(1),
        // The courtyard without its grime decals.
        ["clean"] = new InputScript().Press(Key.D).Wait(1),
    };

    public void Load()
    {
        _courtyard = content.Model("models/courtyard.glb");

        // The model still holds the flat stain quads art/courtyard.py lays in front of the wall
        // (build_stains). The same grime is projected onto the wall as decals instead, so leave them out.
        string[] kept = content.MeshNames(_courtyard).Where(name => name != "cy_stains").ToArray();
        _withoutStains = content.Meshes(_courtyard, kept);

        // The damp is darkened past the grime's own colour so it reads from across the yard: wet brick goes
        // a good deal darker than dry. The model's hidden quads use the same graph, so it isn't changed there.
        var damp = content.Surface("materials/grime.material.cs") with { Color = new Vector3(0.5f) };
        var drip = content.Surface("materials/grime-streak.material.json");
        var rust = content.Surface("materials/rust-streak.material.json");

        // Rising damp along the foot of the wall, solid just under the top of the plinth and faded
        // out below the sill. The grime repeats every metre along the wall, so the band is laid as
        // boxes about a metre wide. The pier stands out of the wall and gets its own.
        float bottom = 0.42f;
        float top = SillZ - 0.05f;
        Band(damp, WallX0, PierX0, bottom, top, 0f);
        Band(damp, PierX1, WallX1, bottom, top, 0f);
        Band(damp, PierX0, PierX1, bottom, top - 0.1f, PierDepth);

        // Dirty water run off the sill, a little wider than the window. A streak fades out at both its
        // sides as well, across the width of its box.
        float half = WindowWidth / 2f + 0.12f;
        _stains.Add((StainBox(WindowX - half, WindowX + half, SillZ - 0.2f, SillZ - 0.95f, 0f, 0.12f), drip));

        // Rust washed down from each anchor plate, in a shallow box, so it stains the brick round
        // the plate and only the plate's rim.
        foreach (float x in new[] { -1.2f, 10.1f })
        {
            _stains.Add((StainBox(x - 0.16f, x + 0.16f, 5.38f, 4.3f, 0f, 0.05f), rust));
        }
    }

    // A band of grime from x0 to x1 that is solid at zSolid and has faded by zFaded, on the wall
    // or on a face standing `outOfWall` in front of it, in boxes about a metre wide.
    private void Band(Surface surface, float x0, float x1, float zSolid, float zFaded, float outOfWall)
    {
        int count = Math.Max(1, (int)MathF.Round(x1 - x0));
        float width = (x1 - x0) / count;
        for (int i = 0; i < count; i++)
        {
            float left = x0 + i * width;
            _stains.Add((StainBox(left, left + width, zSolid, zFaded, outOfWall, 0.12f), surface));
        }
    }

    // A decal box on the wall, from Blender's x and z, projecting into the wall. Its depth runs
    // `depth` either side of a point 2 cm out, where the faces of the bricks are. The grime is
    // solid at the image's V of 0 and fades towards 1, and the box's local +Z is where V is 0,
    // so +Z points to zSolid.
    private static Matrix4x4 StainBox(float x0, float x1, float zSolid, float zFaded, float outOfWall, float depth)
    {
        float width = x1 - x0;
        float height = MathF.Abs(zFaded - zSolid);
        // Blender's (x, y, z) is (x, z, -y) here, so the wall faces +z and its face is at -WallY.
        var centre = new Vector3((x0 + x1) / 2f, (zSolid + zFaded) / 2f, -WallY + outOfWall + 0.02f);
        // The box's +Y looks out of the wall. Its +Z points to the solid edge, and its +X is turned
        // with it so the box isn't mirrored.
        bool fadesUp = zFaded > zSolid;
        var across = fadesUp ? new Vector3(width, 0f, 0f) : new Vector3(-width, 0f, 0f);
        var solid = fadesUp ? new Vector3(0f, -height, 0f) : new Vector3(0f, height, 0f);
        var outward = new Vector3(0f, 0f, 2f * depth);
        return new Matrix4x4(
            across.X, across.Y, across.Z, 0f,
            outward.X, outward.Y, outward.Z, 0f,
            solid.X, solid.Y, solid.Z, 0f,
            centre.X, centre.Y, centre.Z, 1f);
    }

    public void Update(GameTime time)
    {
        if (input.Pressed(Key.M))
        {
            graphics.Materials = !graphics.Materials;
        }

        if (input.Pressed(Key.F))
        {
            graphics.Fog = !graphics.Fog;
        }

        if (input.Pressed(Key.E))
        {
            graphics.AutoExposure = !graphics.AutoExposure;
        }

        if (input.Pressed(Key.D))
        {
            graphics.Decals = !graphics.Decals;
        }
    }

    public void Draw(View view)
    {
        // Standing back at eye height, off to the right and turned left towards the window, as
        // in the reference photo: the low wall, which runs out square from the building, closes
        // the left of the frame, the path runs in to the puddle and the deck fills the right.
        view.Camera(position: new Vector3(5.41f, 1.56f, 4.49f), lookAt: new Vector3(1.01f, 1.36f, -7.11f), fieldOfView: 0.85f);

        // Mid-afternoon. The sun is high on the left and a little in front of the wall, still
        // low enough to rake across the bricks and throw shadows to the right.
        view.Sun(direction: new Vector3(-0.75f, 0.5f, 0.42f), color: new Vector3(7.2f, 6.7f, 5.8f));
        // A clear day's sky, worked out from the sun: brighter towards it, deeper blue overhead
        // and paler at the horizon, so the shade is cooler than the sunlight. It is dim next to
        // the sun, so the shade is a clear step darker than the light. Zenith and Horizon are
        // what it falls back to with Turbidity at 0.
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.2f, 0.38f, 0.8f),
            Horizon = new Vector3(0.75f, 0.8f, 0.85f),
            Ground = new Vector3(0.2f, 0.17f, 0.14f),
            Brightness = 0.75f,
            Turbidity = 2.5f,
            // A light morning fog, thickest on the ground, so the far wall sits a touch back in
            // the air behind the near flagstones. It glows where it looks towards the sun.
            Fog = new FogSettings(Density: 0.004f, Height: 0f, Falloff: 0.15f, SunScatter: 0.6f),
        });
        // A sun close to its real size, so the tree out of frame to the left, 10 m up-sun of
        // the wall, dapples it with crisp leafy shade rather than one soft patch.
        view.Shadows(7f, 3, sunSize: 0.02f);
        // Light probes over the yard, a metre apart, so the sunlit brick warms the shade round it.
        view.LightProbes(new(-2f, -0.5f, -9f), new(9f, 5f, 6f), 1f);
        // A reflection probe at head height in the middle of the yard, so the window and the
        // puddle mirror the wall, the deck and the house rather than only the sky.
        view.ReflectionProbe(YardProbeKey, position: new(3.5f, 1.6f, -1.5f), boxMin: new(-3f, -1f, -10f), boxMax: new(10f, 6f, 7f));

        // The lantern is on, shining every way through its glass. In daylight it only warms
        // the wall just round it. It casts no shadows: the light sits inside the bulb and the lantern round it, and
        // they would hide it.
        view.PointLight(LampKey, LampBulb, color: new Vector3(1.6f, 1.08f, 0.54f), range: 4f, radius: 0.15f, shadows: false);
        // Someone is home: a warm light in the room, seen through the old glass.
        view.PointLight(RoomKey, RoomLamp, color: new Vector3(2.2f, 1.2f, 0.45f), range: 6f, radius: 0.25f);

        // The camera meters the courtyard and sets the brightness itself, then adds about a stop:
        // AgX's Punchy look, the default, deepens the mid-tones, and the sunlit wall should read
        // bright. A touch more contrast and saturation for an afternoon sun, a warm white balance,
        // a cool lift in the shade, and a little bloom round the lamp. The lens is focused on the
        // window, 11 m off, and stopped down to f/8, so the flagstones at our feet soften a little
        // and the window and the wall stay crisp.
        view.Finish(new PostSettings
        {
            FocusDistance = 11f,
            Aperture = 8f,
            AutoExposure = true,
            Exposure = 1.05f,
            Temperature = 0.08f,
            Contrast = 0.25f,
            Saturation = 0.15f,
            Vignette = 0.35f,
            Sharpen = 0.2f,
            Bloom = 0.06f,
            Lift = new Vector3(0f, 0.005f, 0.015f),
            Gain = new Vector3(0.01f, 0.005f, -0.005f),
        });
        view.Model(0, _courtyard, Matrix4x4.Identity, _withoutStains!);

        // The damp, the drip off the sill and the rust, laid onto the wall in the order the old
        // quads were stacked, so the drip lies over the damp.
        for (int i = 0; i < _stains.Count; i++)
        {
            view.Decal(i, _stains[i].Box, _stains[i].Surface);
        }
    }
}
