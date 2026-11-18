using System.Numerics;
using Rendition;
using Super;

namespace Crypt;

/// <summary>
/// A vault lit only by fire. Torches on the pillars throw shadows down the hall, a lantern
/// hung from the vault lights a treasure chest, and an altar of candelabras stands before a
/// statue, with clusters of candles on the floor round it: well over a hundred lights in all.
/// The flames are particles that glow, and a cold shaft from a grate high up gives the
/// fire something to stand against. A cold mist lies over the floor, and V switches it off.
/// </summary>
public sealed class CryptGame(Content content, Input input, Graphics graphics) : IGame
{
    private const int FloorKey = 1;
    private const int WallKey = 10;
    private const int PillarKey = 100;
    private const int TorchKey = 200;
    private const int AltarKey = 300;
    private const int StatueKey = 310;
    private const int ChestKey = 320;
    public const int LanternKey = 330;
    private const int GrateKey = 340;
    private const int MistKey = 350;
    private const int BarrelKey = 400;
    private const int CandelabraKey = 500;
    private const int AltarFlameKey = 600;
    private const int FlameKey = 100_000;

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

    private readonly LooseCandles _candles = new();
    private readonly List<(ModelRef Model, Vector3 At, float Turn, Vector3[] Wicks)> _candelabras = [];

    private CryptModels _models = null!;
    private float _seconds;

    private const float AltarZ = -7.2f;
    private const float AltarTop = 1.1f;
    private static readonly Vector3 ChestAt = new(0f, 0f, -3.2f);
    private static readonly Vector3 LanternAt = new(0f, 3.1f, -3.1f);

    public void Load()
    {
        _models = CryptModels.Load(content, new Vector3(HallWidth, HallHeight, HallFront - HallBack), AltarTop);

        // The candelabras on the altar, the tall one in the middle.
        _candelabras.Add((_models.SevenCandles, new Vector3(0f, AltarTop, AltarZ), 0.3f, SevenWicks));
        _candelabras.Add((_models.ThreeCandles, new Vector3(-0.8f, AltarTop, AltarZ + 0.1f), 0.2f, ThreeWicks));
        _candelabras.Add((_models.ThreeCandles, new Vector3(0.8f, AltarTop, AltarZ + 0.1f), -0.2f, ThreeWicks));
        _candelabras.Add((_models.OneCandle, new Vector3(-1.15f, AltarTop, AltarZ - 0.2f), 0f, OneWick));
        _candelabras.Add((_models.OneCandle, new Vector3(1.15f, AltarTop, AltarZ - 0.2f), 0f, OneWick));

        // Clusters of loose candles: along the altar's step, and round each pillar's foot.
        var random = new Random(7);
        _candles.Scatter(random, new Vector3(0f, 0.18f, AltarZ + 0.85f), new Vector2(1.9f, 0.35f), 34);
        _candles.Scatter(random, new Vector3(-1.85f, 0.18f, AltarZ + 0.2f), new Vector2(0.25f, 0.6f), 10);
        _candles.Scatter(random, new Vector3(1.85f, 0.18f, AltarZ + 0.2f), new Vector2(0.25f, 0.6f), 10);
        foreach (float z in PillarRows)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                _candles.Ring(random, new Vector3(side * PillarX, 0.3f, z), 0.28f, 0.42f, 7);
                _candles.Scatter(random, new Vector3(side * (PillarX - 0.85f), 0f, z + 0.2f), new Vector2(0.25f, 0.25f), 5);
            }
        }
    }

    public void Update(GameTime time)
    {
        _seconds = time.Tick / (float)time.TicksPerSecond;
        if (input.Pressed(Key.V))
        {
            graphics.Atmosphere.FogVolumes = !graphics.Atmosphere.FogVolumes;
        }
    }

    public void Draw(View view)
    {
        view.Camera(new Vector3(0.35f, 1.75f, 3.6f), new Vector3(0f, 1.2f, -7f), 0.9f);
        view.Surroundings.Sky(new SkySettings
        {
            Zenith = new Vector3(0.02f, 0.025f, 0.04f),
            Horizon = new Vector3(0.02f, 0.02f, 0.03f),
            Ground = new Vector3(0.01f, 0.01f, 0.01f),
            Brightness = 0.05f,
            // A faint, dusty air, just enough for the torches and the lantern to glow in it. Any
            // thicker and the far end of the room goes hazy. There is no sun to scatter.
            Fog = new FogSettings(Density: 0.008f, Height: 0f, Falloff: 0.15f, SunScatter: 0f),
        });
        view.Effects.Finish(new PostSettings
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
        });

        // The hall is one baked space, "crypt". Its box holds the walls and the vault with a little to spare, and its
        // probes light the lantern as it swings. Everything that stands still is drawn Static, so its light is
        // baked. The torches, the altar flames and the candles stay Mixed, so they keep their real-time flicker, and
        // the lantern and its chain move, so they are never baked. See the README.
        view.Indirect.BakedLighting("crypt", new Vector3(-5.2f, -0.2f, -10.2f), new Vector3(5.2f, 5.5f, 4.7f), 0.8f);

        DrawHall(view);
        DrawTorches(view);
        DrawAltar(view);
        _candles.Draw(view, _seconds);
        DrawChestAndLantern(view);
        DrawColdAir(view);
    }

    /// <summary>
    /// What isn't fire: a cold shaft from a grate high in the left wall, and a cold mist over the whole floor, in a
    /// box 0.8 m deep with soft edges, in patches drifting slowly towards the door, so the torches and candles glow
    /// in it. V switches the mist off.
    /// </summary>
    private static void DrawColdAir(View view)
    {
        var grate = new Vector3(-HallWidth / 2f + 0.35f, 4.3f, -1.2f);
        view.Lights.Spot(GrateKey, new SpotLight(grate, Vector3.Normalize(new Vector3(2.95f, -4.3f, 0.2f)), new Vector3(14f, 22f, 40f), 9f) { InnerAngle = 0.12f, OuterAngle = 0.3f });

        var mist = new FogVolumeSettings(0.3f)
        {
            EdgeFade = 0.5f,
            Noise = 0.6f,
            NoiseSize = 1.5f,
            Wind = new Vector3(0.05f, 0f, 0.12f),
        };
        view.Effects.FogVolume(MistKey, FogShape.Box,
            Matrix4x4.CreateScale(HallWidth, 0.8f, HallFront - HallBack) * Matrix4x4.CreateTranslation(0f, 0.2f, (HallFront + HallBack) / 2f),
            mist);
    }

    private void DrawHall(View view)
    {
        float middleZ = (HallFront + HallBack) / 2f;
        view.Model(FloorKey, _models.Floor, Matrix4x4.CreateTranslation(0f, 0f, middleZ), new ModelOptions { Static = true });
        view.Model(WallKey, _models.SideWall, Matrix4x4.CreateTranslation(-HallWidth / 2f - 0.3f, HallHeight / 2f, middleZ), new ModelOptions { Static = true });
        view.Model(WallKey + 1, _models.SideWall, Matrix4x4.CreateTranslation(HallWidth / 2f + 0.3f, HallHeight / 2f, middleZ), new ModelOptions { Static = true });
        view.Model(WallKey + 2, _models.EndWall, Matrix4x4.CreateTranslation(0f, HallHeight / 2f, HallBack - 0.3f), new ModelOptions { Static = true });
        view.Model(WallKey + 3, _models.EndWall, Matrix4x4.CreateTranslation(0f, HallHeight / 2f, HallFront + 0.3f), new ModelOptions { Static = true });
        view.Model(WallKey + 4, _models.Ceiling, Matrix4x4.CreateTranslation(0f, HallHeight + 0.2f, middleZ), new ModelOptions { Static = true });

        int key = PillarKey;
        foreach (float z in PillarRows)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                var foot = new Vector3(side * PillarX, 0f, z);
                view.Model(key++, _models.PillarFoot, Matrix4x4.CreateTranslation(foot + new Vector3(0f, 0.15f, 0f)), new ModelOptions { Static = true });
                view.Model(key++, _models.Pillar, Matrix4x4.CreateTranslation(foot + new Vector3(0f, 0.3f + ((HallHeight - 0.6f) / 2f), 0f)), new ModelOptions { Static = true });
                view.Model(key++, _models.PillarFoot, Matrix4x4.CreateTranslation(foot + new Vector3(0f, HallHeight - 0.15f, 0f)), new ModelOptions { Static = true });
            }
        }

        // Barrels stacked against the right wall, between the pillars.
        var barrels = new (Vector3 At, float Turn)[]
        {
            (new(3.75f, 0f, -1.3f), 0.4f), (new(3.8f, 0f, -0.45f), 1.9f), (new(3.3f, 0f, -0.9f), 3.1f),
            (new(-3.8f, 0f, -4.4f), 0.9f),
        };
        for (int i = 0; i < barrels.Length; i++)
        {
            view.Model(BarrelKey + i, _models.Barrel, Matrix4x4.CreateRotationY(barrels[i].Turn) * Matrix4x4.CreateTranslation(barrels[i].At), new ModelOptions { Static = true });
        }
    }

    private void DrawTorches(View view)
    {
        int i = 0;
        foreach (float z in new[] { PillarRows[0], PillarRows[2] })
        {
            foreach (float side in new[] { -1f, 1f })
            {
                // A torch leaning out from the pillar's side towards the aisle.
                var bracket = new Vector3(side * (PillarX - 0.42f), 2.3f, z);
                var lean = Matrix4x4.CreateRotationZ(side * 0.35f);
                view.Model(TorchKey + (i * 2), ModelRef.Cylinder, Matrix4x4.CreateScale(0.06f, 0.55f, 0.06f) * lean * Matrix4x4.CreateTranslation(bracket), new ModelOptions { Color = new Vector3(0.12f, 0.07f, 0.04f), Static = true });
                view.Model(TorchKey + (i * 2) + 1, ModelRef.Cylinder, Matrix4x4.CreateScale(0.1f, 0.1f, 0.1f) * lean * Matrix4x4.CreateTranslation(bracket + Vector3.Transform(new Vector3(0f, 0.27f, 0f), lean)), new ModelOptions { Color = new Vector3(0.05f, 0.04f, 0.035f), Static = true });
                var fire = bracket + Vector3.Transform(new Vector3(0f, 0.34f, 0f), lean);
                float flicker = 1f + (0.1f * MathF.Sin(_seconds * 11f + i * 2f)) + (0.07f * MathF.Sin(_seconds * 23f + i));
                // The flame is a hand from the stone, so a radius a little bigger than the fire keeps
                // the pillar from blowing out white beside it. Its halo is doubled, so it reads in
                // the faint air without thickening the air for everything else.
                view.Lights.Point(TorchKey + i, new PointLight(fire + new Vector3(0f, 0.12f, 0f), new Vector3(3.4f, 1.5f, 0.45f) * flicker, 9f) { Radius = 0.5f, Volumetric = 2f });
                CryptFire.Draw(view, FlameKey + (i * 1000), CryptFire.Torch, fire, _seconds);
                i++;
            }
        }
    }

    private void DrawAltar(View view)
    {
        view.Model(AltarKey, _models.Step, Matrix4x4.CreateTranslation(0f, 0.09f, AltarZ + 0.1f), new ModelOptions { Static = true });
        view.Model(AltarKey + 1, _models.Altar, Matrix4x4.CreateTranslation(0f, 0.18f + ((AltarTop - 0.18f) / 2f), AltarZ), new ModelOptions { Static = true });
        view.Model(AltarKey + 2, _models.Plinth, Matrix4x4.CreateTranslation(0f, 0.35f, HallBack + 0.75f), new ModelOptions { Static = true });
        view.Model(StatueKey, _models.Statue, Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateTranslation(0f, 0.7f, HallBack + 1.25f), new ModelOptions { Static = true });

        int flame = 0;
        for (int c = 0; c < _candelabras.Count; c++)
        {
            var (model, at, turn, wicks) = _candelabras[c];
            var place = Matrix4x4.CreateRotationY(turn) * Matrix4x4.CreateTranslation(at);
            view.Model(CandelabraKey + c, model, place, new ModelOptions { Static = true });
            foreach (var wick in wicks)
            {
                var tip = Vector3.Transform(wick, place);
                float flicker = 1f + (0.12f * MathF.Sin(_seconds * 13f + flame * 1.7f));
                view.Lights.Point(AltarFlameKey + flame, new PointLight(tip + new Vector3(0f, 0.03f, 0f), new Vector3(0.3f, 0.15f, 0.05f) * flicker, 2.5f) { Shadows = false });
                CryptFire.Draw(view, FlameKey + 10_000 + (flame * 1000), CryptFire.CandleFlame, tip, _seconds);
                flame++;
            }
        }

        // One light for the altar's candles together, so the statue and candelabras cast shadows.
        float glow = 1f + (0.06f * MathF.Sin(_seconds * 9f));
        view.Lights.Point(AltarFlameKey + 99, new PointLight(new Vector3(0f, AltarTop + 0.7f, AltarZ + 0.2f), new Vector3(1.2f, 0.55f, 0.18f) * glow, 6f));
    }

    private void DrawChestAndLantern(View view)
    {
        view.Model(ChestKey, _models.Chest, Matrix4x4.CreateRotationY(-0.35f) * Matrix4x4.CreateTranslation(ChestAt), new ModelOptions { Static = true });

        // The lantern hangs on a chain from the vault, swinging a little.
        float swing = 0.04f * MathF.Sin(_seconds * 1.3f);
        var hang = new Vector3(LanternAt.X, HallHeight, LanternAt.Z);
        var chain = Matrix4x4.CreateRotationX(swing);
        float length = HallHeight - LanternAt.Y;
        var at = hang + Vector3.Transform(new Vector3(0f, -length, 0f), chain);
        view.Model(LanternKey, ModelRef.Cylinder, Matrix4x4.CreateScale(0.015f, length, 0.015f) * Matrix4x4.CreateTranslation(0f, -length / 2f, 0f) * chain * Matrix4x4.CreateTranslation(hang), new Vector3(0.05f));
        view.Model(LanternKey + 1, _models.Lantern, Matrix4x4.CreateScale(1.6f) * Matrix4x4.CreateTranslation(0f, -0.47f, 0f) * chain * Matrix4x4.CreateTranslation(at));
        var flame = at + Vector3.Transform(new Vector3(0f, -0.32f, 0f), chain);
        var beam = at + Vector3.Transform(new Vector3(0f, -0.52f, 0f), chain);
        view.Lights.Spot(LanternKey, new SpotLight(beam, Vector3.Normalize(ChestAt + new Vector3(0f, 0.3f, 0f) - beam), new Vector3(30f, 21f, 10f), 6f) { InnerAngle = 0.3f, OuterAngle = 0.55f });
        view.Lights.Point(LanternKey + 1, new PointLight(flame, new Vector3(0.35f, 0.22f, 0.08f), 3f) { Shadows = false });
    }
}
