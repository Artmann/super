using System.Numerics;
using Rendition;
using Super;

namespace Courtyard;

/// <summary>
/// A sunlit courtyard modelled in Blender (art/courtyard.py): a brick wall with an arched
/// window, a dry-stone wall, a flagstone path through trodden mud and a puddle. Its bricks, stone,
/// mud, wood, rust and window glass are the material graphs, put on the model by its cook sidecar.
/// M turns the materials off and on, to judge the lighting and the shapes on their own, and F the fog.
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

    private ModelRef _courtyard;

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        // The courtyard in plain clay, through the same key a player presses.
        ["plain"] = new InputScript().Press(Key.M).Wait(1),
        // The courtyard in clear air, to compare with the fog.
        ["clear"] = new InputScript().Press(Key.F).Wait(1),
        // The courtyard at a fixed exposure, to compare with what the camera meters.
        ["fixed"] = new InputScript().Press(Key.E).Wait(1),
    };

    public void Load()
    {
        _courtyard = content.Model("models/courtyard.glb");
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

        // The camera meters the courtyard and sets the brightness itself, then adds about a third of a
        // stop so the shade stays open and the sunlit wall bright. AgX, the default curve, keeps the
        // sunlit bricks apart where ACES ran them into one orange, but reads softer, so contrast and
        // saturation are up to match. A warm white balance for the afternoon, a cool lift in the
        // shade, and a little bloom round the lamp.
        view.Finish(new PostSettings
        {
            AutoExposure = true,
            Exposure = 0.3f,
            Temperature = 0.08f,
            Contrast = 0.3f,
            Saturation = 0.2f,
            Vignette = 0.35f,
            Sharpen = 0.2f,
            Bloom = 0.06f,
            Lift = new Vector3(0f, 0.005f, 0.015f),
            Gain = new Vector3(0.01f, 0.005f, -0.005f),
        });
        view.Model(0, _courtyard, Matrix4x4.Identity);
    }
}
