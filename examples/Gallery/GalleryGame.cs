using System.Numerics;
using Rendition;
using Super;

namespace Gallery;

/// <summary>
/// Plaster rooms along one corridor, lit the way the engine lights things today: a sun, a sky,
/// light probes, reflection probes and ordinary point lights. An entrance hall with a long
/// skylight opens onto a corridor whose colonnade throws shadows all the way down it. Off the
/// corridor are a colour room with a marble bust between a red wall and a blue one, a whale
/// room with a bronze whale under a skylight, a dark lamp room, and a chapel lit by a swinging
/// lantern and candles. A courtyard waits at the end behind tinted glass. The camera tours the
/// rooms on its own; F lets you fly it with WASD and the mouse, and F again goes back to the tour.
/// </summary>
public sealed class GalleryGame(Content content, Input input) : IGame
{
    /// <summary>Where the sun is: high in the north-east, so it rakes through the colonnade.</summary>
    public static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.8f, 0.9f, -0.35f));

    /// <summary>The entrance hall's skylight, in the roof 6 m up.</summary>
    public static readonly Area HallSkylight = new(new(3.5f, 6f, 0.5f), new(5f, 6.3f, 7f));

    /// <summary>The colour room's clerestory window, in its east wall above the corridor roof.</summary>
    public static readonly Area ColourWindow = new(new(-2.3f, 4.9f, -8f), new(-2f, 6.6f, -5f));

    /// <summary>The whale room's skylight, in the roof 7 m up.</summary>
    public static readonly Area WhaleSkylight = new(new(-4.2f, 7f, -18.5f), new(-2.6f, 7.3f, -15.5f));

    /// <summary>The top of the bust's plinth, placed where the sun through the window lands on the bust.</summary>
    public static readonly Vector3 BustAt = Below(ColourWindow.Centre, 1.3f) with { Y = 1f };

    /// <summary>The top of the whale's plinth, placed where the sun through the skylight lands on the whale.</summary>
    public static readonly Vector3 WhaleAt = Below(WhaleSkylight.Centre, 1.05f) with { Y = 0.5f };

    /// <summary>The glass screen between the corridor and the courtyard.</summary>
    public const float ScreenZ = -42.15f;

    /// <summary>The lamp room's three lamps: the middle of each glowing disc.</summary>
    public static readonly Vector3[] Lamps = [new(-9f, 2.9f, -22.8f), new(-6.5f, 2.9f, -22.8f), new(-4f, 2.9f, -22.8f)];

    private const int DynamicKey = 10_000;
    private const int LampLightKey = 100;
    private const int LanternKey = 110;
    private const int CandleKey = 120;
    private const int WhaleProbeKey = 1;
    private const int ColourProbeKey = 2;
    private const int LampProbeKey = 3;
    private const int HallProbeKey = 4;
    private const int FlameKey = 1_000_000;

    private const float BrassRadius = 0.35f;
    private const float FlySpeed = 4f;
    private const float TurnPerPixel = 0.0025f;

    /// <summary>The chapel lantern hangs from a hook in the roof, over the aisle.</summary>
    private static readonly Vector3 LanternHook = new(-6.2f, 8f, -36f);
    private const float LanternChain = 4.4f;

    /// <summary>A candle flame: small glowing puffs that read as one teardrop.</summary>
    private static readonly ParticleEffect Flame = new ParticleEffect().Add(new ParticleEmitter(count: 24, lifetime: 0.3f)
        .Over(1f)
        .FromSphere(0.004f)
        .Velocity(new Vector3(0f, 0.14f, 0f))
        .Scatter(0.006f)
        .Gravity(new Vector3(0f, 0.3f, 0f))
        .Size(0.022f, 0.004f)
        .Color(new Vector3(1f, 0.82f, 0.45f), new Vector3(1f, 0.38f, 0.08f))
        .Glow(5f)
        .Fade(0.05f, 0.6f));

    private readonly List<(Vector3 Foot, float Height)> _candles = [];
    private readonly Tour _tour = new();
    private Building _building = null!;
    private ModelRef _bust;
    private ModelRef _whale;
    private ModelRef _brass;
    private ModelRef _metal;
    private ModelRef _wax;
    private float _seconds;
    private long _tick;

    private bool _flying;
    private Vector3 _position;
    private Vector3 _previousPosition;
    private float _yaw;
    private float _pitch;
    private float _previousYaw;
    private float _previousPitch;

    /// <summary>A box in the world, from one corner to the other.</summary>
    public readonly record struct Area(Vector3 Min, Vector3 Max)
    {
        public Vector3 Centre => (Min + Max) / 2f;
    }

    public void Load()
    {
        var plaster = content.Texture(Plaster(128, 0.08f, 7));
        var faint = content.Texture(Plaster(128, 0.04f, 11));
        var glow = content.Shader("glow");

        var materials = new Dictionary<string, Surface>
        {
            ["plaster"] = new() { Color = new Vector3(0.8f, 0.79f, 0.76f), ColorTexture = plaster, Roughness = 0.92f },
            ["floor"] = new() { Color = new Vector3(0.52f, 0.5f, 0.47f), ColorTexture = plaster, Roughness = 0.8f },
            ["white"] = new() { Color = new Vector3(0.9f, 0.9f, 0.88f), ColorTexture = faint, Roughness = 0.7f },
            ["red"] = new() { Color = new Vector3(0.72f, 0.05f, 0.04f), ColorTexture = faint, Roughness = 0.92f },
            ["blue"] = new() { Color = new Vector3(0.04f, 0.14f, 0.65f), ColorTexture = faint, Roughness = 0.92f },
            ["gravel"] = new() { Color = new Vector3(0.42f, 0.42f, 0.36f), ColorTexture = plaster, Roughness = 0.95f },
            ["flags"] = new() { Color = new Vector3(0.62f, 0.6f, 0.56f), ColorTexture = plaster, Roughness = 0.85f },
            ["wood"] = new() { Color = new Vector3(0.3f, 0.18f, 0.1f), ColorTexture = faint, Roughness = 0.75f },
            ["frame"] = new() { Color = new Vector3(0.12f), Roughness = 0.5f, Metallic = 1f },
            ["glass"] = new() { Color = new Vector3(0.45f, 0.8f, 0.72f), Roughness = 0.05f, Opacity = 0.2f },
            ["water"] = new() { Color = new Vector3(0.1f, 0.18f, 0.2f), Roughness = 0.05f },
            ["orange"] = new() { Color = new Vector3(0.9f, 0.35f, 0.04f), Roughness = 0.6f },
            ["leaves"] = new() { Color = new Vector3(0.16f, 0.32f, 0.08f), ColorTexture = plaster, Roughness = 0.9f },
            ["panel"] = new() { Color = new Vector3(0.9f, 0.88f, 0.8f), Roughness = 0.5f, Shader = glow, Parameters = [new Vector4(2.4f, 2.1f, 1.6f, 0f)] },
            ["lamp"] = new() { Color = new Vector3(1f, 0.95f, 0.85f), Roughness = 0.5f, Shader = glow, Parameters = [new Vector4(10f, 7.5f, 4.2f, 0f)] },
            ["shade"] = new() { Color = new Vector3(0.06f, 0.06f, 0.065f), Roughness = 0.4f, Metallic = 1f },
        };

        _building = new Building(content, materials);
        _building.Build();

        _bust = content.Model("models/marble_bust_01/marble_bust_01.gltf");
        _whale = content.Model("models/bronze_whale_statue/bronze_whale_statue.gltf");
        _brass = content.Mesh(Shapes.Sphere(BrassRadius, 64, 32, new Surface { Color = new Vector3(0.95f, 0.72f, 0.38f), Metallic = 1f, Roughness = 0.22f }));
        _metal = content.Mesh(Shapes.Box(Vector3.One, materials["shade"]));
        _wax = content.Mesh(Shapes.Cylinder(0.5f, 0.5f, 1f, 16, new Surface { Color = new Vector3(0.9f, 0.84f, 0.7f), Roughness = 0.6f }));

        // Seven candles along the altar, of different heights.
        float[] heights = [0.22f, 0.3f, 0.18f, 0.34f, 0.2f, 0.28f, 0.24f];
        for (int i = 0; i < heights.Length; i++)
        {
            _candles.Add((new Vector3(-10.15f, 1.08f, -37.2f + i * 0.4f), heights[i]));
        }

        _position = _tour.At(0f).Position;
        _previousPosition = _position;
    }

    public void Update(GameTime time)
    {
        _tick = time.Tick;
        _seconds = time.Tick / (float)time.TicksPerSecond;

        if (input.Pressed(Key.F))
        {
            _flying = !_flying;
            if (_flying)
            {
                // Take over from wherever the tour is, looking the same way.
                var shot = _tour.At(_seconds);
                var look = Vector3.Normalize(shot.Target - shot.Position);
                _position = shot.Position;
                _yaw = MathF.Atan2(-look.X, -look.Z);
                _pitch = MathF.Asin(look.Y);
            }
        }

        input.CaptureMouse = _flying;
        _previousPosition = _position;
        _previousYaw = _yaw;
        _previousPitch = _pitch;
        if (!_flying)
        {
            return;
        }

        var turn = input.MouseDelta * TurnPerPixel;
        _yaw -= turn.X;
        _pitch = Math.Clamp(_pitch - turn.Y, -1.5f, 1.5f);

        var forward = Forward(_yaw, 0f);
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var step = Vector3.Zero;
        step += forward * (Axis(Key.W, Key.S));
        step += right * (Axis(Key.D, Key.A));
        step.Y += Axis(Key.E, Key.Q);
        _position += step * (FlySpeed * time.TickSeconds);
    }

    public void Draw(View view)
    {
        float seconds = view.SecondsSince(0);
        if (_flying)
        {
            var position = Vector3.Lerp(_previousPosition, _position, view.Blend);
            float yaw = _previousYaw + (_yaw - _previousYaw) * view.Blend;
            float pitch = _previousPitch + (_pitch - _previousPitch) * view.Blend;
            view.Camera(position, position + Forward(yaw, pitch), 1f);
        }
        else
        {
            view.Camera(_tour.At(seconds));
        }

        view.Sun(SunDirection, new Vector3(7.2f, 6.8f, 6f));
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.2f, 0.38f, 0.8f),
            Horizon = new Vector3(0.75f, 0.8f, 0.85f),
            Ground = new Vector3(0.2f, 0.18f, 0.15f),
            Brightness = 1f,
            Turbidity = 2.5f,
            Fog = new FogSettings(Density: 0.004f, Height: 0f, Falloff: 0.1f, SunScatter: 0.6f),
        });

        // A crisp sun with two cascades: the last ends 15 m round the camera's target, well short
        // of the far end of the 42 m corridor.
        view.Shadows(5f, cascades: 2, sunSize: 0.02f);
        view.AmbientOcclusion(0.8f, 1f);

        // One box of probes over the whole gallery. At most 4,096 fit, so they're 1.65 m apart.
        // It's kept on top of the probes that follow the camera: without it the rooms' walls and
        // floors came out blotchy, and the building greyer from the overview.
        view.LightProbes(new Vector3(-11.8f, -0.4f, -56.6f), new Vector3(7.8f, 8.6f, 10.6f), 1.65f);

        // The four reflection probes a frame can have, in the rooms with something shiny.
        view.ReflectionProbe(WhaleProbeKey, new Vector3(-6.5f, 2.5f, -15f), new Vector3(-11f, 0f, -19.85f), new Vector3(-2.3f, 7f, -10.15f));
        view.ReflectionProbe(ColourProbeKey, new Vector3(-6.5f, 2f, -5f), new Vector3(-11f, 0f, -9.85f), new Vector3(-2.3f, 7f, -0.3f));
        view.ReflectionProbe(LampProbeKey, new Vector3(-6.5f, 1.5f, -25f), new Vector3(-11f, 0f, -29.85f), new Vector3(-2.3f, 4.5f, -20.15f));
        view.ReflectionProbe(HallProbeKey, new Vector3(0f, 2f, 5f), new Vector3(-6f, 0f, 0f), new Vector3(6f, 6f, 10f));

        view.Finish(new PostSettings
        {
            AutoExposure = true,
            Exposure = 1.1f,
            Contrast = 0.15f,
            Saturation = 0.05f,
            Vignette = 0.25f,
            Bloom = 0.04f,
            Sharpen = 0.15f,
        });

        var pieces = _building.Pieces;
        for (int i = 0; i < pieces.Count; i++)
        {
            view.Model(1 + i, pieces[i].Mesh, pieces[i].Transform);
        }

        view.Model(DynamicKey, _bust, Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(BustAt));
        view.Model(DynamicKey + 1, _whale, Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(WhaleAt));

        DrawLampRoom(view, seconds);
        DrawChapel(view, seconds);
    }

    private void DrawLampRoom(View view, float seconds)
    {
        // Three hanging lamps with big glowing discs, so their shadows are soft and wide.
        for (int i = 0; i < Lamps.Length; i++)
        {
            var disc = Lamps[i];
            view.PointLight(LampLightKey + i, disc - new Vector3(0f, 0.15f, 0f), new Vector3(2.2f, 1.6f, 0.95f), range: 5.5f, radius: 0.35f);
        }

        // The brass sphere rolls back and forth under the lamps, so their soft shadows swing round it.
        float x = -6.5f + 2.6f * MathF.Sin(seconds * 0.45f);
        var roll = Matrix4x4.CreateRotationZ(-x / BrassRadius);
        view.Model(DynamicKey + 10, _brass, roll * Matrix4x4.CreateTranslation(x, BrassRadius, -24.2f));
    }

    private void DrawChapel(View view, float seconds)
    {
        // The lantern swings on its chain along the aisle.
        float swing = 0.14f * MathF.Sin(seconds * 1.6f);
        var chain = Matrix4x4.CreateRotationZ(swing);
        var lantern = LanternHook + Vector3.Transform(new Vector3(0f, -LanternChain, 0f), chain);
        var place = chain * Matrix4x4.CreateTranslation(lantern);
        view.Model(DynamicKey + 20, _metal, Matrix4x4.CreateScale(0.015f, LanternChain, 0.015f) * Matrix4x4.CreateTranslation(0f, LanternChain / 2f, 0f) * place);
        view.Model(DynamicKey + 21, _metal, Matrix4x4.CreateScale(0.28f, 0.06f, 0.28f) * Matrix4x4.CreateTranslation(0f, -0.03f, 0f) * place);
        view.Model(DynamicKey + 22, _metal, Matrix4x4.CreateScale(0.24f, 0.03f, 0.24f) * Matrix4x4.CreateTranslation(0f, -0.48f, 0f) * place);
        int post = 0;
        foreach (var (px, pz) in new[] { (-0.11f, -0.11f), (0.11f, -0.11f), (-0.11f, 0.11f), (0.11f, 0.11f) })
        {
            view.Model(DynamicKey + 23 + post++, _metal, Matrix4x4.CreateScale(0.02f, 0.42f, 0.02f) * Matrix4x4.CreateTranslation(px, -0.27f, pz) * place);
        }

        view.Model(DynamicKey + 30, _wax, Matrix4x4.CreateScale(0.05f, 0.12f, 0.05f) * Matrix4x4.CreateTranslation(0f, -0.405f, 0f) * place);
        var wick = Vector3.Transform(new Vector3(0f, -0.335f, 0f), place);
        float lanternFlicker = 1f + 0.08f * MathF.Sin(_seconds * 9f) + 0.05f * MathF.Sin(_seconds * 23f);
        view.PointLight(LanternKey, wick + new Vector3(0f, 0.05f, 0f), new Vector3(1.8f, 1.05f, 0.4f) * lanternFlicker, range: 9f, radius: 0.04f);
        DrawFlame(view, FlameKey, wick);

        // A row of candles on the altar, each flickering on its own, with no shadows.
        for (int i = 0; i < _candles.Count; i++)
        {
            var (foot, height) = _candles[i];
            view.Model(DynamicKey + 40 + i, _wax, Matrix4x4.CreateScale(0.045f, height, 0.045f) * Matrix4x4.CreateTranslation(foot + new Vector3(0f, height / 2f, 0f)));
            var tip = foot + new Vector3(0f, height + 0.012f, 0f);
            float flicker = 1f + 0.15f * MathF.Sin(_seconds * 13f + i * 1.7f) + 0.08f * MathF.Sin(_seconds * 29f + i);
            view.PointLight(CandleKey + i, tip + new Vector3(0f, 0.03f, 0f), new Vector3(0.4f, 0.19f, 0.055f) * flicker, range: 3f, shadows: false);
            DrawFlame(view, FlameKey + (i + 1) * 1000, tip);
        }
    }

    /// <summary>A flame that never goes out: this second's puffs and last second's, each under its own key.</summary>
    private void DrawFlame(View view, int key, Vector3 at)
    {
        int second = (int)_seconds;
        float into = _seconds - second;
        view.Particles(key + (second % 500), Flame, at, into);
        view.Particles(key + ((second + 499) % 500), Flame, at, into + 1f);
    }

    private float Axis(Key positive, Key negative) =>
        (input.Down(positive) ? 1f : 0f) - (input.Down(negative) ? 1f : 0f);

    private static Vector3 Forward(float yaw, float pitch) =>
        new(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));

    /// <summary>Where the sun through an opening lands at a height.</summary>
    private static Vector3 Below(Vector3 opening, float height) =>
        opening - SunDirection * ((opening.Y - height) / SunDirection.Y);

    /// <summary>
    /// A tileable light-grey noise, for plaster: values from 1 - <paramref name="depth"/> up to 1,
    /// in blotches about <paramref name="size"/> / <paramref name="cells"/> pixels across, with a
    /// finer layer on top. It wraps at the edges so it repeats without a seam.
    /// </summary>
    private static GeneratedTexture Plaster(int size, float depth, int seed)
    {
        var random = new Random(seed);
        float[] Lattice(int cells)
        {
            var values = new float[cells * cells];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (float)random.NextDouble();
            }

            return values;
        }

        float Sample(float[] lattice, int cells, float x, float y)
        {
            float fx = x * cells / size;
            float fy = y * cells / size;
            int x0 = (int)fx;
            int y0 = (int)fy;
            float tx = fx - x0;
            float ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float At(int cx, int cy) => lattice[(cy % cells) * cells + (cx % cells)];
            float top = At(x0, y0) + (At(x0 + 1, y0) - At(x0, y0)) * tx;
            float bottom = At(x0, y0 + 1) + (At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * tx;
            return top + (bottom - top) * ty;
        }

        var coarse = Lattice(8);
        var fine = Lattice(32);
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float noise = Sample(coarse, 8, x, y) * 0.65f + Sample(fine, 32, x, y) * 0.35f;
                byte shade = (byte)Math.Clamp(MathF.Round(255f * (1f - depth * noise)), 0f, 255f);
                int at = (y * size + x) * 4;
                pixels[at] = shade;
                pixels[at + 1] = shade;
                pixels[at + 2] = shade;
                pixels[at + 3] = 255;
            }
        }

        return new GeneratedTexture(size, pixels);
    }
}
