using System.Numerics;
using Rendition;
using Super;

namespace Courtyard;

/// <summary>
/// A sunlit courtyard modelled in Blender (art/courtyard.py): a brick wall with an arched
/// window, a dry-stone wall, a flagstone path through trodden mud and a puddle. Its bricks, stone,
/// mud, wood, rust and window glass are the material graphs, put on the model by its cook sidecar.
/// M turns the materials off and on, to judge the lighting and the shapes on their own, F the fog, V the volumetric fog and D the
/// decals: the grime on the wall, the gravel spilled off the path and the dirt, rust, moss, wet ground and boot prints
/// listed in content/decals/courtyard.json.
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

    // The gravel's colour a little under full, so the mud's own colour shows through between the grains of grit, and
    // its stones' bumps whole.
    private static readonly DecalBlend GravelBlend = new(Color: 0.9f, Normal: 1f);

    /// <summary>The seed the gravel spills are placed from.</summary>
    public const int GravelSeed = 19;

    /// <summary>The folder of hand-placed decal lists, and the Courtyard's own list in it.</summary>
    public const string DecalFolder = "decals";

    /// <summary>The Courtyard's list in <see cref="DecalFolder"/>, content/decals/courtyard.json.</summary>
    public const string DecalListId = "courtyard";

    private ModelRef _courtyard;
    private readonly List<(Matrix4x4 Box, Surface Surface)> _stains = [];
    private readonly List<(Matrix4x4 Box, Surface Surface, DecalBlend Blend)> _placed = [];
    private List<Matrix4x4> _gravelBoxes = [];
    private Surface? _gravel;

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        // The courtyard in plain clay, through the same key a player presses.
        ["plain"] = new InputScript().Press(Key.M).Wait(1),
        // The courtyard in clear air, to compare with the fog.
        ["clear"] = new InputScript().Press(Key.F).Wait(1),
        // The courtyard at a fixed exposure, to compare with what the camera meters.
        ["fixed"] = new InputScript().Press(Key.E).Wait(1),
        // The courtyard without its decals: the grime on the wall, the gravel off the path and the hand-placed list.
        ["clean"] = new InputScript().Press(Key.D).Wait(1),
        // The courtyard with the plain height fog, to compare with the volumetric fog.
        ["plain-fog"] = new InputScript().Press(Key.V).Wait(1),
    };

    public void Load()
    {
        _courtyard = content.Model("models/courtyard.glb");

        // The damp is darkened past the grime's own colour so it reads from across the yard: wet brick goes
        // a good deal darker than dry.
        var damp = content.Surface("materials/grime.material.cs") with { Color = new Vector3(0.5f) };
        var drip = content.Surface("materials/grime-streak.material.json");
        var rust = content.Surface("materials/rust-streak.material.json");
        foreach (var (box, kind) in WallStains.Boxes())
        {
            var surface = kind switch
            {
                Stain.Damp => damp,
                Stain.Drip => drip,
                _ => rust,
            };
            _stains.Add((box, surface));
        }

        // The hand-placed decals: dirt round everything that stands on the ground, rust, moss, wet ground and boot
        // prints. Each names its material, and a list that names one that wasn't cooked stops here.
        foreach (var decal in content.LoadAll<DecalList>(DecalFolder)[DecalListId].Decals)
        {
            _placed.Add((decal.Box(), content.Surface(decal.Material), decal.Blend));
        }

        // Gravel kicked off the flagstones into the mud, along both edges of the path and round the barrel.
        _gravel = content.Surface("materials/gravel.material.cs");
        _gravelBoxes = GravelSpills.Boxes(GravelSeed);
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

        if (input.Pressed(Key.V))
        {
            graphics.VolumetricFog = !graphics.VolumetricFog;
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
        // Water stands 4.5 cm below the yard's zero, where the script's puddle bottoms out, so
        // the hollow in the mud fills, the deepest boot prints hold a little, and the mud
        // darkens for the last 3.5 cm down to it, so the yard round the hollow stays dry.
        view.Puddles(-0.045f, band: 0.035f);

        // A fresh breeze across the yard sways the grass, and a little of it reaches the ivy and the tree's crown,
        // whose materials sway less in the cook sidecar.
        view.Wind(new Vector3(0.6f, 0f, -0.3f), 0.35f);

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
        view.Model(0, _courtyard, Matrix4x4.Identity);

        // The damp, the drip off the sill and the rust, laid onto the wall so the drip lies over the damp.
        for (int i = 0; i < _stains.Count; i++)
        {
            view.Decal(i, _stains[i].Box, _stains[i].Surface);
        }

        // The hand-placed decals, keyed after the stains and drawn before the gravel, so the gravel round the barrel
        // lies over the dirt there.
        for (int i = 0; i < _placed.Count; i++)
        {
            view.Decal(_stains.Count + i, _placed[i].Box, _placed[i].Surface, _placed[i].Blend);
        }

        // The gravel off the path, keyed after both.
        int first = _stains.Count + _placed.Count;
        for (int i = 0; i < _gravelBoxes.Count; i++)
        {
            view.Decal(first + i, _gravelBoxes[i], _gravel!, GravelBlend);
        }
    }
}
