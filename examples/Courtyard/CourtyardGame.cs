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
        // Sharper sun shadows for a picture wider than full HD, where the 2048 maps' texels would show.
        graphics.Shadowing.ShadowResolution = input.ScreenSize.X > 1920f ? 4096 : 2048;

        if (input.Pressed(Key.M))
        {
            graphics.Surfaces.Materials = !graphics.Surfaces.Materials;
        }

        if (input.Pressed(Key.F))
        {
            graphics.Atmosphere.Fog = !graphics.Atmosphere.Fog;
        }

        if (input.Pressed(Key.E))
        {
            graphics.Picture.AutoExposure = !graphics.Picture.AutoExposure;
        }

        if (input.Pressed(Key.D))
        {
            graphics.Surfaces.Decals = !graphics.Surfaces.Decals;
        }

        if (input.Pressed(Key.V))
        {
            graphics.Atmosphere.VolumetricFog = !graphics.Atmosphere.VolumetricFog;
        }
    }

    public void Draw(View view)
    {
        // Standing back at eye height, off to the right and turned left towards the window, as
        // in the reference photo: the low wall, which runs out square from the building, closes
        // the left of the frame, the path runs in to the puddle and the deck fills the right.
        view.Camera(position: new Vector3(5.41f, 1.56f, 4.49f), lookAt: new Vector3(1.01f, 1.36f, -7.11f), fieldOfView: 0.85f);
        CourtyardLight.Daylight(view);
        CourtyardLight.Lamps(view);
        CourtyardLight.GradePicture(view);

        // The yard is one baked space, "courtyard", over the whole model with a little to spare. The model never
        // moves, so it is static and its light is baked: the sun, which stays Mixed so the real-time cascades still
        // shadow what is close to the camera, and the room lamp, which shines out through the window glass. The
        // decals (the damp, the drip and the rust) stay live. The grass, ivy and leaves are part of the model and are
        // lightmapped with the rest. See the README.
        view.Indirect.BakedLighting("courtyard", new Vector3(-16.5f, -0.5f, -18.75f), new Vector3(14.5f, 10f, 12.5f), 1.6f);
        view.Model(0, _courtyard, Matrix4x4.Identity, new ModelOptions { Static = true });

        // The damp, the drip off the sill and the rust, laid onto the wall so the drip lies over the damp.
        for (int i = 0; i < _stains.Count; i++)
        {
            view.Effects.Decal(i, _stains[i].Box, _stains[i].Surface);
        }

        // The hand-placed decals, keyed after the stains and drawn before the gravel, so the gravel round the barrel
        // lies over the dirt there.
        for (int i = 0; i < _placed.Count; i++)
        {
            view.Effects.Decal(_stains.Count + i, _placed[i].Box, _placed[i].Surface, _placed[i].Blend);
        }

        // The gravel off the path, keyed after both.
        int first = _stains.Count + _placed.Count;
        for (int i = 0; i < _gravelBoxes.Count; i++)
        {
            view.Effects.Decal(first + i, _gravelBoxes[i], _gravel!, GravelBlend);
        }
    }
}
