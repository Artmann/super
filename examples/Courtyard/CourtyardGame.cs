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
public sealed class CourtyardGame(World world, Content content, Input input, Graphics graphics) : IGame
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
        CourtyardYard.Spawn(world, content.Model("models/courtyard.glb"));

        // The decals are spawned in the order they lie: the damp, the drip off the sill and the rust on the wall, so the
        // drip lies over the damp; then the hand-placed ones; then the gravel, so the gravel round the barrel lies over
        // the dirt there.
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
            world.Spawn(Transform.From(box), new Decal(surface));
        }

        // The hand-placed decals: dirt round everything that stands on the ground, rust, moss, wet ground and boot
        // prints. Each names its material, and a list that names one that wasn't cooked stops here.
        foreach (var decal in content.LoadAll<DecalList>(DecalFolder)[DecalListId].Decals)
        {
            world.Spawn(Transform.From(decal.Box()), new Decal(content.Surface(decal.Material), decal.Blend));
        }

        // Gravel kicked off the flagstones into the mud, along both edges of the path and round the barrel.
        var gravel = content.Surface("materials/gravel.material.cs");
        foreach (var box in GravelSpills.Boxes(GravelSeed))
        {
            world.Spawn(Transform.From(box), new Decal(gravel, GravelBlend));
        }
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
}
