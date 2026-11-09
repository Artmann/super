using System.Numerics;
using Rendition;
using Super;

namespace Crypt;

/// <summary>
/// A vault lit only by fire. Torches on the pillars throw shadows down the hall, a lantern
/// hung from the vault lights a treasure chest, and an altar of candelabras stands before a
/// statue, with clusters of candles on the floor round it: well over a hundred lights in all.
/// The flames are particles that glow, and a cold shaft from a grate high up gives the
/// fire something to stand against.
/// </summary>
public sealed class CryptGame(Content content) : IGame
{
    private const int FloorKey = 1;
    private const int WallKey = 10;
    private const int PillarKey = 100;
    private const int TorchKey = 200;
    private const int AltarKey = 300;
    private const int StatueKey = 310;
    private const int ChestKey = 320;
    private const int LanternKey = 330;
    private const int GrateKey = 340;
    private const int BarrelKey = 400;
    private const int CandelabraKey = 500;
    private const int AltarFlameKey = 600;
    private const int CandleKey = 1000;
    private const int FlameKey = 100_000;

    private const float HallWidth = 9f;
    private const float HallFront = 4f;
    private const float HallBack = -9.5f;
    private const float HallHeight = 5f;

    /// <summary>
    /// A candle's flame: small glowing puffs packed close so they read as one teardrop, warm
    /// yellow at the wick going to orange at the tip, born all through a second so one
    /// second's worth runs on into the next.
    /// </summary>
    public static readonly ParticleEmitter Flame = new ParticleEmitter(count: 24, lifetime: 0.3f)
        .Over(1f)
        .FromSphere(0.004f)
        .Velocity(new Vector3(0f, 0.14f, 0f))
        .Scatter(0.006f)
        .Gravity(new Vector3(0f, 0.3f, 0f))
        .Size(0.022f, 0.004f)
        .Color(new Vector3(1f, 0.82f, 0.45f), new Vector3(1f, 0.38f, 0.08f))
        .Glow(5f)
        .Fade(0.05f, 0.6f);

    /// <summary>A torch's fire, the same flame bigger and wilder.</summary>
    public static readonly ParticleEmitter TorchFire = new ParticleEmitter(count: 70, lifetime: 0.5f)
        .Over(1f)
        .FromSphere(0.04f)
        .Velocity(new Vector3(0f, 0.45f, 0f))
        .Scatter(0.05f)
        .Gravity(new Vector3(0f, 0.9f, 0f))
        .Size(0.13f, 0.02f)
        .Color(new Vector3(1f, 0.78f, 0.4f), new Vector3(0.9f, 0.25f, 0.05f))
        .Glow(4f)
        .Fade(0.08f, 0.55f)
        .Spin(1f);

    /// <summary>Embers drifting up from a torch, few and slow.</summary>
    public static readonly ParticleEmitter Embers = new ParticleEmitter(count: 6, lifetime: 1.6f)
        .Over(1f)
        .FromSphere(0.05f)
        .Velocity(new Vector3(0f, 0.5f, 0f))
        .Scatter(0.25f)
        .Size(0.012f, 0.004f)
        .Color(new Vector3(1f, 0.55f, 0.15f))
        .Glow(8f)
        .Fade(0.05f, 0.4f);

    private static readonly ParticleEffect CandleFlame = new ParticleEffect().Add(Flame);
    private static readonly ParticleEffect Torch = new ParticleEffect().Add(TorchFire).Add(Embers);

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

    private readonly List<(Vector3 Foot, float Height, float Width)> _candles = [];
    private readonly List<(ModelRef Model, Vector3 At, float Turn, Vector3[] Wicks)> _candelabras = [];

    private ModelRef _floor;
    private ModelRef _sideWall;
    private ModelRef _endWall;
    private ModelRef _ceiling;
    private ModelRef _pillar;
    private ModelRef _pillarFoot;
    private ModelRef _step;
    private ModelRef _altar;
    private ModelRef _plinth;
    private ModelRef _statue;
    private ModelRef _chest;
    private ModelRef _lantern;
    private ModelRef _barrel;
    private float _seconds;

    private const float AltarZ = -7.2f;
    private const float AltarTop = 1.1f;
    private static readonly Vector3 ChestAt = new(0f, 0f, -3.2f);
    private static readonly Vector3 LanternAt = new(0f, 3.1f, -3.1f);

    public void Load()
    {
        var floor = Stone("floor");
        var wall = Stone("wall");
        var blocks = Stone("blocks");

        _floor = content.Mesh(Shapes.Plane(new Vector2(HallWidth, HallFront - HallBack), surface: floor, uvPerMetre: 0.4f));
        _sideWall = content.Mesh(Shapes.Box(new Vector3(0.6f, HallHeight, HallFront - HallBack), wall, uvPerMetre: 0.35f));
        _endWall = content.Mesh(Shapes.Box(new Vector3(HallWidth + 1.2f, HallHeight, 0.6f), wall, uvPerMetre: 0.35f));
        _ceiling = content.Mesh(Shapes.Box(new Vector3(HallWidth + 1.2f, 0.4f, HallFront - HallBack + 1.2f), blocks, uvPerMetre: 0.5f));
        _pillar = content.Mesh(Shapes.Cylinder(0.34f, 0.38f, HallHeight - 0.6f, 24, blocks, uvPerMetre: 0.6f));
        _pillarFoot = content.Mesh(Shapes.Box(new Vector3(1f, 0.3f, 1f), blocks, uvPerMetre: 0.6f));
        _step = content.Mesh(Shapes.Box(new Vector3(4.2f, 0.18f, 2.6f), floor, uvPerMetre: 0.4f));
        _altar = content.Mesh(Shapes.Box(new Vector3(2.6f, AltarTop - 0.18f, 1f), blocks, uvPerMetre: 0.6f));
        _plinth = content.Mesh(Shapes.Box(new Vector3(1.8f, 0.7f, 1.4f), blocks, uvPerMetre: 0.6f));

        _statue = content.Model("props/gothic_statue/gothic_statue_1k.gltf");
        _chest = content.Model("props/treasure_chest/treasure_chest_1k.gltf");
        _lantern = content.Model("props/Lantern_01/Lantern_01_1k.gltf");
        _barrel = content.Model("props/wine_barrel_01/wine_barrel_01_1k.gltf");
        var seven = content.Model("props/brass_candleholders/brass_candleholders_1k.gltf#seven");
        var three = content.Model("props/brass_candleholders/brass_candleholders_1k.gltf#three");
        var one = content.Model("props/brass_candleholders/brass_candleholders_1k.gltf#one");

        // The candelabras on the altar, the tall one in the middle.
        _candelabras.Add((seven, new Vector3(0f, AltarTop, AltarZ), 0.3f, SevenWicks));
        _candelabras.Add((three, new Vector3(-0.8f, AltarTop, AltarZ + 0.1f), 0.2f, ThreeWicks));
        _candelabras.Add((three, new Vector3(0.8f, AltarTop, AltarZ + 0.1f), -0.2f, ThreeWicks));
        _candelabras.Add((one, new Vector3(-1.15f, AltarTop, AltarZ - 0.2f), 0f, OneWick));
        _candelabras.Add((one, new Vector3(1.15f, AltarTop, AltarZ - 0.2f), 0f, OneWick));

        // Clusters of loose candles: along the altar's step, and round each pillar's foot.
        var random = new Random(7);
        Scatter(random, new Vector3(0f, 0.18f, AltarZ + 0.85f), new Vector2(1.9f, 0.35f), 34);
        Scatter(random, new Vector3(-1.85f, 0.18f, AltarZ + 0.2f), new Vector2(0.25f, 0.6f), 10);
        Scatter(random, new Vector3(1.85f, 0.18f, AltarZ + 0.2f), new Vector2(0.25f, 0.6f), 10);
        foreach (float z in PillarRows)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                Ring(random, new Vector3(side * PillarX, 0.3f, z), 0.28f, 0.42f, 7);
                Scatter(random, new Vector3(side * (PillarX - 0.85f), 0f, z + 0.2f), new Vector2(0.25f, 0.25f), 5);
            }
        }
    }

    private Surface Stone(string name) => new()
    {
        Color = Vector3.One,
        Roughness = 1f,
        ColorTexture = content.Texture($"textures/{name}.jpg"),
        NormalTexture = content.Texture($"textures/{name}-normal.jpg"),
        SurfaceTexture = content.Texture($"textures/{name}-surface.png"),
    };

    private void Scatter(Random random, Vector3 middle, Vector2 half, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var at = middle + new Vector3(((float)random.NextDouble() * 2f - 1f) * half.X, 0f, ((float)random.NextDouble() * 2f - 1f) * half.Y);
            AddCandle(random, at);
        }
    }

    private void Ring(Random random, Vector3 middle, float inner, float outer, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float turn = ((i + (float)random.NextDouble() * 0.6f) / count) * MathF.Tau;
            float reach = inner + ((float)random.NextDouble() * (outer - inner));
            AddCandle(random, middle + new Vector3(MathF.Cos(turn) * reach, 0f, MathF.Sin(turn) * reach));
        }
    }

    private void AddCandle(Random random, Vector3 foot)
    {
        float height = 0.06f + (0.22f * MathF.Pow((float)random.NextDouble(), 1.5f));
        float width = 0.035f + (0.035f * (float)random.NextDouble());
        _candles.Add((foot, height, width));
    }

    public void Update(GameTime time) => _seconds = time.Tick / (float)time.TicksPerSecond;

    public void Draw(View view)
    {
        view.Camera(new Vector3(0.35f, 1.75f, 3.6f), new Vector3(0f, 1.2f, -7f), 0.9f);
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.02f, 0.025f, 0.04f),
            Horizon = new Vector3(0.02f, 0.02f, 0.03f),
            Ground = new Vector3(0.01f, 0.01f, 0.01f),
            Brightness = 0.05f,
            // A faint, dusty air, just enough for the torches and the lantern to glow in it. Any
            // thicker and the far end of the room goes hazy. There is no sun to scatter.
            Fog = new FogSettings(Density: 0.008f, Height: 0f, Falloff: 0.15f, SunScatter: 0f),
        });
        view.Finish(new PostSettings
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

        DrawHall(view);
        DrawTorches(view);
        DrawAltar(view);
        DrawCandles(view);
        DrawChestAndLantern(view);

        // A cold shaft from a grate high in the left wall, the only light that isn't fire.
        var grate = new Vector3(-HallWidth / 2f + 0.35f, 4.3f, -1.2f);
        view.SpotLight(GrateKey, grate, Vector3.Normalize(new Vector3(2.95f, -4.3f, 0.2f)), new Vector3(14f, 22f, 40f), range: 9f, innerAngle: 0.12f, outerAngle: 0.3f);
    }

    private void DrawHall(View view)
    {
        float middleZ = (HallFront + HallBack) / 2f;
        view.Model(FloorKey, _floor, Matrix4x4.CreateTranslation(0f, 0f, middleZ));
        view.Model(WallKey, _sideWall, Matrix4x4.CreateTranslation(-HallWidth / 2f - 0.3f, HallHeight / 2f, middleZ));
        view.Model(WallKey + 1, _sideWall, Matrix4x4.CreateTranslation(HallWidth / 2f + 0.3f, HallHeight / 2f, middleZ));
        view.Model(WallKey + 2, _endWall, Matrix4x4.CreateTranslation(0f, HallHeight / 2f, HallBack - 0.3f));
        view.Model(WallKey + 3, _endWall, Matrix4x4.CreateTranslation(0f, HallHeight / 2f, HallFront + 0.3f));
        view.Model(WallKey + 4, _ceiling, Matrix4x4.CreateTranslation(0f, HallHeight + 0.2f, middleZ));

        int key = PillarKey;
        foreach (float z in PillarRows)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                var foot = new Vector3(side * PillarX, 0f, z);
                view.Model(key++, _pillarFoot, Matrix4x4.CreateTranslation(foot + new Vector3(0f, 0.15f, 0f)));
                view.Model(key++, _pillar, Matrix4x4.CreateTranslation(foot + new Vector3(0f, 0.3f + ((HallHeight - 0.6f) / 2f), 0f)));
                view.Model(key++, _pillarFoot, Matrix4x4.CreateTranslation(foot + new Vector3(0f, HallHeight - 0.15f, 0f)));
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
            view.Model(BarrelKey + i, _barrel, Matrix4x4.CreateRotationY(barrels[i].Turn) * Matrix4x4.CreateTranslation(barrels[i].At));
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
                view.Model(TorchKey + (i * 2), ModelRef.Cylinder, Matrix4x4.CreateScale(0.06f, 0.55f, 0.06f) * lean * Matrix4x4.CreateTranslation(bracket), new Vector3(0.12f, 0.07f, 0.04f));
                view.Model(TorchKey + (i * 2) + 1, ModelRef.Cylinder, Matrix4x4.CreateScale(0.1f, 0.1f, 0.1f) * lean * Matrix4x4.CreateTranslation(bracket + Vector3.Transform(new Vector3(0f, 0.27f, 0f), lean)), new Vector3(0.05f, 0.04f, 0.035f));
                var fire = bracket + Vector3.Transform(new Vector3(0f, 0.34f, 0f), lean);
                float flicker = 1f + (0.1f * MathF.Sin(_seconds * 11f + i * 2f)) + (0.07f * MathF.Sin(_seconds * 23f + i));
                // The flame is a hand from the stone, so a radius a little bigger than the fire keeps
                // the pillar from blowing out white beside it. Its halo is doubled, so it reads in
                // the faint air without thickening the air for everything else.
                view.PointLight(TorchKey + i, fire + new Vector3(0f, 0.12f, 0f), new Vector3(3.4f, 1.5f, 0.45f) * flicker, range: 9f, radius: 0.5f, volumetric: 2f);
                DrawFire(view, FlameKey + (i * 1000), Torch, fire);
                i++;
            }
        }
    }

    private void DrawAltar(View view)
    {
        view.Model(AltarKey, _step, Matrix4x4.CreateTranslation(0f, 0.09f, AltarZ + 0.1f));
        view.Model(AltarKey + 1, _altar, Matrix4x4.CreateTranslation(0f, 0.18f + ((AltarTop - 0.18f) / 2f), AltarZ));
        view.Model(AltarKey + 2, _plinth, Matrix4x4.CreateTranslation(0f, 0.35f, HallBack + 0.75f));
        view.Model(StatueKey, _statue, Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateTranslation(0f, 0.7f, HallBack + 1.25f));

        int flame = 0;
        for (int c = 0; c < _candelabras.Count; c++)
        {
            var (model, at, turn, wicks) = _candelabras[c];
            var place = Matrix4x4.CreateRotationY(turn) * Matrix4x4.CreateTranslation(at);
            view.Model(CandelabraKey + c, model, place);
            foreach (var wick in wicks)
            {
                var tip = Vector3.Transform(wick, place);
                float flicker = 1f + (0.12f * MathF.Sin(_seconds * 13f + flame * 1.7f));
                view.PointLight(AltarFlameKey + flame, tip + new Vector3(0f, 0.03f, 0f), new Vector3(0.3f, 0.15f, 0.05f) * flicker, range: 2.5f, shadows: false);
                DrawFire(view, FlameKey + 10_000 + (flame * 1000), CandleFlame, tip);
                flame++;
            }
        }

        // One light for the altar's candles together, so the statue and candelabras cast shadows.
        float glow = 1f + (0.06f * MathF.Sin(_seconds * 9f));
        view.PointLight(AltarFlameKey + 99, new Vector3(0f, AltarTop + 0.7f, AltarZ + 0.2f), new Vector3(1.2f, 0.55f, 0.18f) * glow, range: 6f);
    }

    private void DrawCandles(View view)
    {
        var wax = new Vector3(0.85f, 0.78f, 0.62f);
        for (int i = 0; i < _candles.Count; i++)
        {
            var (foot, height, width) = _candles[i];
            view.Model(CandleKey + i, ModelRef.Cylinder, Matrix4x4.CreateScale(width, height, width) * Matrix4x4.CreateTranslation(foot + new Vector3(0f, height * 0.5f, 0f)), wax);
            var wick = foot + new Vector3(0f, height + 0.012f, 0f);
            float flicker = 1f + (0.15f * MathF.Sin(_seconds * 13f + i));
            view.PointLight(CandleKey + i, wick + new Vector3(0f, 0.03f, 0f), new Vector3(0.16f, 0.075f, 0.022f) * flicker, range: 1.6f, shadows: false);
            DrawFire(view, FlameKey + 100_000 + (i * 1000), CandleFlame, wick);
        }
    }

    private void DrawChestAndLantern(View view)
    {
        view.Model(ChestKey, _chest, Matrix4x4.CreateRotationY(-0.35f) * Matrix4x4.CreateTranslation(ChestAt));

        // The lantern hangs on a chain from the vault, swinging a little.
        float swing = 0.04f * MathF.Sin(_seconds * 1.3f);
        var hang = new Vector3(LanternAt.X, HallHeight, LanternAt.Z);
        var chain = Matrix4x4.CreateRotationX(swing);
        float length = HallHeight - LanternAt.Y;
        var at = hang + Vector3.Transform(new Vector3(0f, -length, 0f), chain);
        view.Model(LanternKey, ModelRef.Cylinder, Matrix4x4.CreateScale(0.015f, length, 0.015f) * Matrix4x4.CreateTranslation(0f, -length / 2f, 0f) * chain * Matrix4x4.CreateTranslation(hang), new Vector3(0.05f));
        view.Model(LanternKey + 1, _lantern, Matrix4x4.CreateScale(1.6f) * Matrix4x4.CreateTranslation(0f, -0.47f, 0f) * chain * Matrix4x4.CreateTranslation(at));
        var flame = at + Vector3.Transform(new Vector3(0f, -0.32f, 0f), chain);
        var beam = at + Vector3.Transform(new Vector3(0f, -0.52f, 0f), chain);
        view.SpotLight(LanternKey, beam, Vector3.Normalize(ChestAt + new Vector3(0f, 0.3f, 0f) - beam), new Vector3(30f, 21f, 10f), range: 6f, innerAngle: 0.3f, outerAngle: 0.55f);
        view.PointLight(LanternKey + 1, flame, new Vector3(0.35f, 0.22f, 0.08f), range: 3f, shadows: false);
    }

    // A fire that never stops: each second's flames under their own key, this one's and the last's still dying out.
    private void DrawFire(View view, int key, ParticleEffect effect, Vector3 at)
    {
        int second = (int)_seconds;
        float into = _seconds - second;
        view.Particles(key + (second % 500), effect, at, into);
        view.Particles(key + ((second + 499) % 500), effect, at, into + 1f);
    }
}
