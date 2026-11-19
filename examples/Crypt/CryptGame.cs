using System.Numerics;
using Rendition;
using Examples;
using Super;

namespace Crypt;

/// <summary>
/// A vault lit only by fire. Torches on the pillars throw shadows down the hall, a lantern
/// hung from the vault lights a treasure chest, and an altar of candelabras stands before a
/// statue, with clusters of candles on the floor round it: well over a hundred lights in all.
/// The flames are particles that glow, and a cold shaft from a grate high up gives the
/// fire something to stand against. A cold mist lies over the floor, and V switches it off.
/// </summary>
public sealed class CryptGame(World world, Content content, Input input, Graphics graphics) : IGame
{
    public const float HallWidth = 9f;
    public const float HallFront = 4f;
    public const float HallBack = -9.5f;
    public const float HallHeight = 5f;

    private static readonly float[] PillarRows = [0.5f, -2.8f, -6.1f];
    private const float PillarX = 2.7f;

    // Where the candelabras' own candles burn, from their model files, and where each stands.
    private static readonly Vector3[] SevenWicks =
    [
        new(0.007f, 0.801f, 0.006f), new(0.178f, 0.717f, 0.006f), new(-0.166f, 0.717f, 0.006f),
        new(0.086f, 0.722f, 0.157f), new(-0.09f, 0.722f, -0.141f), new(0.095f, 0.722f, -0.153f),
        new(-0.079f, 0.724f, 0.145f),
    ];

    private static readonly Vector3[] ThreeWicks = [new(0.004f, 0.377f, 0.004f), new(-0.102f, 0.359f, 0.004f), new(0.11f, 0.359f, 0.004f)];
    private static readonly Vector3[] OneWick = [new(0.004f, 0.342f, 0.004f)];

    private const float AltarZ = -7.2f;
    private const float AltarTop = 1.1f;
    private static readonly Vector3 ChestAt = new(0f, 0f, -3.2f);
    private static readonly Vector3 LanternAt = new(0f, 3.1f, -3.1f);

    private CryptModels _models = null!;
    private SwingingLantern _lantern = null!;

    public void Load()
    {
        _models = CryptModels.Load(content, new Vector3(HallWidth, HallHeight, HallFront - HallBack), AltarTop);

        world.Spawn(new Camera(new Vector3(0.35f, 1.75f, 3.6f), new Vector3(0f, 1.2f, -7f), 0.9f));
        world.Spawn(
            new Sky(new SkySettings
            {
                Zenith = new Vector3(0.02f, 0.025f, 0.04f),
                Horizon = new Vector3(0.02f, 0.02f, 0.03f),
                Ground = new Vector3(0.01f, 0.01f, 0.01f),
                Brightness = 0.05f,
                // A faint, dusty air, just enough for the torches and the lantern to glow in it. Any
                // thicker and the far end of the room goes hazy. There is no sun to scatter.
                Fog = new FogSettings(Density: 0.008f, Height: 0f, Falloff: 0.15f, SunScatter: 0f),
            }),
            new Finish(new PostSettings
            {
                Exposure = 0.7f,
                Contrast = 0.12f,
                Saturation = 0.05f,
                Bloom = 0.06f,
                Vignette = 0.4f,
                Grain = 0.012f,
                Sharpen = 0.2f,
                Lift = new Vector3(0.004f, 0.006f, 0.014f),
                Gain = new Vector3(0.04f, 0.015f, -0.03f),
            }));

        // Everything that stands still is Static, so its light is baked. The torches, the altar flames and the
        // candles stay Mixed, so they keep their real-time flicker, and the lantern and its chain move, so they are
        // never baked. See the README.
        Hall();
        Torches();
        Altar();
        Candles();
        ChestAndLantern();
        ColdShaft();
        BakedSpaceAndMist();
    }

    public void Update(GameTime time)
    {
        float seconds = time.Tick / (float)time.TicksPerSecond;
        Flicker.Update(world, seconds);

        _lantern.Swing(seconds);

        if (input.Pressed(Key.V))
        {
            graphics.Atmosphere.FogVolumes = !graphics.Atmosphere.FogVolumes;
        }
    }

    /// <summary>
    /// The baked space, and a cold mist over the whole floor, in a box 0.8 m deep with soft edges, in patches drifting
    /// slowly towards the door, so the torches and candles glow in it. V switches the mist off.
    /// </summary>
    private void BakedSpaceAndMist()
    {
        // The hall is one baked space, "crypt". Its box holds the walls and the vault with a little to spare, and its
        // probes light the lantern as it swings.
        world.Spawn(new BakedLighting("crypt", new Vector3(-5.2f, -0.2f, -10.2f), new Vector3(5.2f, 5.5f, 4.7f), 0.8f));

        var mist = new FogVolumeSettings(0.3f)
        {
            EdgeFade = 0.5f,
            Noise = 0.6f,
            NoiseSize = 1.5f,
            Wind = new Vector3(0.05f, 0f, 0.12f),
        };
        world.Spawn(
            new Transform(new Vector3(0f, 0.2f, (HallFront + HallBack) / 2f), Quaternion.Identity, new Vector3(HallWidth, 0.8f, HallFront - HallBack)),
            new FogVolume(FogShape.Box, mist));
    }

    /// <summary>A model that stands still, so its light is baked.</summary>
    private void Still(Transform transform, ModelRef model) => world.Spawn(transform, new Model(model), new Static());

    private void Still(Transform transform, ModelRef model, Vector3 color) => world.Spawn(transform, new Model(model, color), new Static());

    private void Hall()
    {
        float middleZ = (HallFront + HallBack) / 2f;
        Still(Transform.At(new Vector3(0f, 0f, middleZ)), _models.Floor);
        Still(Transform.At(new Vector3(-HallWidth / 2f - 0.3f, HallHeight / 2f, middleZ)), _models.SideWall);
        Still(Transform.At(new Vector3(HallWidth / 2f + 0.3f, HallHeight / 2f, middleZ)), _models.SideWall);
        Still(Transform.At(new Vector3(0f, HallHeight / 2f, HallBack - 0.3f)), _models.EndWall);
        Still(Transform.At(new Vector3(0f, HallHeight / 2f, HallFront + 0.3f)), _models.EndWall);
        Still(Transform.At(new Vector3(0f, HallHeight + 0.2f, middleZ)), _models.Ceiling);

        foreach (float z in PillarRows)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                var foot = new Vector3(side * PillarX, 0f, z);
                Still(Transform.At(foot + new Vector3(0f, 0.15f, 0f)), _models.PillarFoot);
                Still(Transform.At(foot + new Vector3(0f, 0.3f + ((HallHeight - 0.6f) / 2f), 0f)), _models.Pillar);
                Still(Transform.At(foot + new Vector3(0f, HallHeight - 0.15f, 0f)), _models.PillarFoot);
            }
        }

        // Barrels stacked against the right wall, between the pillars.
        var barrels = new (Vector3 At, float Turn)[]
        {
            (new(3.75f, 0f, -1.3f), 0.4f), (new(3.8f, 0f, -0.45f), 1.9f), (new(3.3f, 0f, -0.9f), 3.1f),
            (new(-3.8f, 0f, -4.4f), 0.9f),
        };
        foreach (var (at, turn) in barrels)
        {
            Still(new Transform(at, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn), Vector3.One), _models.Barrel);
        }
    }

    private void Torches()
    {
        int i = 0;
        foreach (float z in new[] { PillarRows[0], PillarRows[2] })
        {
            foreach (float side in new[] { -1f, 1f })
            {
                // A torch leaning out from the pillar's side towards the aisle.
                var bracket = new Vector3(side * (PillarX - 0.42f), 2.3f, z);
                var lean = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, side * 0.35f);
                Still(new Transform(bracket, lean, new Vector3(0.06f, 0.55f, 0.06f)), ModelRef.Cylinder, new Vector3(0.12f, 0.07f, 0.04f));
                Still(new Transform(bracket + Vector3.Transform(new Vector3(0f, 0.27f, 0f), lean), lean, new Vector3(0.1f)), ModelRef.Cylinder, new Vector3(0.05f, 0.04f, 0.035f));
                var fire = bracket + Vector3.Transform(new Vector3(0f, 0.34f, 0f), lean);

                // The flame is a hand from the stone, so a radius a little bigger than the fire keeps
                // the pillar from blowing out white beside it. Its halo is doubled, so it reads in
                // the faint air without thickening the air for everything else.
                var color = new Vector3(3.4f, 1.5f, 0.45f);
                world.Spawn(
                    new PointLight(fire + new Vector3(0f, 0.12f, 0f), color, 9f) { Radius = 0.5f, Volumetric = 2f },
                    new Flicker(color, 0.1f, 11f, i * 2f, Ripple: 0.07f, RippleSpeed: 23f, RipplePhase: i));
                world.Spawn(Transform.At(fire), new Emitter(CryptFire.Torch));
                i++;
            }
        }
    }

    private void Altar()
    {
        Still(Transform.At(new Vector3(0f, 0.09f, AltarZ + 0.1f)), _models.Step);
        Still(Transform.At(new Vector3(0f, 0.18f + ((AltarTop - 0.18f) / 2f), AltarZ)), _models.Altar);
        Still(Transform.At(new Vector3(0f, 0.35f, HallBack + 0.75f)), _models.Plinth);
        Still(Transform.At(new Vector3(0f, 0.7f, HallBack + 1.25f), 1.2f), _models.Statue);

        // The candelabras on the altar, the tall one in the middle.
        var candelabras = new (ModelRef Model, Vector3 At, float Turn, Vector3[] Wicks)[]
        {
            (_models.SevenCandles, new Vector3(0f, AltarTop, AltarZ), 0.3f, SevenWicks),
            (_models.ThreeCandles, new Vector3(-0.8f, AltarTop, AltarZ + 0.1f), 0.2f, ThreeWicks),
            (_models.ThreeCandles, new Vector3(0.8f, AltarTop, AltarZ + 0.1f), -0.2f, ThreeWicks),
            (_models.OneCandle, new Vector3(-1.15f, AltarTop, AltarZ - 0.2f), 0f, OneWick),
            (_models.OneCandle, new Vector3(1.15f, AltarTop, AltarZ - 0.2f), 0f, OneWick),
        };

        int flame = 0;
        foreach (var (model, at, turn, wicks) in candelabras)
        {
            var place = new Transform(at, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn), Vector3.One);
            Still(place, model);
            foreach (var wick in wicks)
            {
                var tip = Vector3.Transform(wick, place.Matrix);
                var color = new Vector3(0.3f, 0.15f, 0.05f);
                world.Spawn(new PointLight(tip + new Vector3(0f, 0.03f, 0f), color, 2.5f) { Shadows = false }, new Flicker(color, 0.12f, 13f, flame * 1.7f));
                world.Spawn(Transform.At(tip), new Emitter(CryptFire.CandleFlame));
                flame++;
            }
        }

        // One light for the altar's candles together, so the statue and candelabras cast shadows.
        var glow = new Vector3(1.2f, 0.55f, 0.18f);
        world.Spawn(new PointLight(new Vector3(0f, AltarTop + 0.7f, AltarZ + 0.2f), glow, 6f), new Flicker(glow, 0.06f, 9f, 0f));
    }

    /// <summary>Clusters of loose candles: along the altar's step, and round each pillar's foot.</summary>
    private void Candles()
    {
        var candles = new LooseCandles(world);
        var random = new Random(7);
        candles.Scatter(random, new Vector3(0f, 0.18f, AltarZ + 0.85f), new Vector2(1.9f, 0.35f), 34);
        candles.Scatter(random, new Vector3(-1.85f, 0.18f, AltarZ + 0.2f), new Vector2(0.25f, 0.6f), 10);
        candles.Scatter(random, new Vector3(1.85f, 0.18f, AltarZ + 0.2f), new Vector2(0.25f, 0.6f), 10);
        foreach (float z in PillarRows)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                candles.Ring(random, new Vector3(side * PillarX, 0.3f, z), 0.28f, 0.42f, 7);
                candles.Scatter(random, new Vector3(side * (PillarX - 0.85f), 0f, z + 0.2f), new Vector2(0.25f, 0.25f), 5);
            }
        }
    }

    /// <summary>The chest, and the lantern over it on a chain from the vault.</summary>
    private void ChestAndLantern()
    {
        Still(new Transform(ChestAt, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.35f), Vector3.One), _models.Chest);
        _lantern = new SwingingLantern(world, _models.Lantern, new Vector3(LanternAt.X, HallHeight, LanternAt.Z), LanternAt, ChestAt + new Vector3(0f, 0.3f, 0f));
    }

    /// <summary>A cold shaft from a grate high in the left wall, for the fire to stand against.</summary>
    private void ColdShaft()
    {
        var grate = new Vector3(-HallWidth / 2f + 0.35f, 4.3f, -1.2f);
        world.Spawn(new SpotLight(grate, Vector3.Normalize(new Vector3(2.95f, -4.3f, 0.2f)), new Vector3(14f, 22f, 40f), 9f) { InnerAngle = 0.12f, OuterAngle = 0.3f });
    }
}
