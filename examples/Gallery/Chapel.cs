using System.Numerics;
using Rendition;
using Super;

namespace Gallery;

/// <summary>What moves and flickers in the chapel: a lantern swinging on its chain over the aisle, and candles on the altar.</summary>
internal sealed class Chapel
{
    private const int LanternKey = 110;
    private const int CandleKey = 120;
    private const int FlameKey = 1_000_000;
    private const int ModelKey = GalleryPlan.DynamicKey + 20;

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
    private readonly ModelRef _metal;
    private readonly ModelRef _wax;

    /// <summary>Makes the lantern's metal in <paramref name="shade"/>, and the wax.</summary>
    public Chapel(Content content, Surface shade)
    {
        _metal = content.Mesh(Shapes.Box(Vector3.One, shade));
        _wax = content.Mesh(Shapes.Cylinder(0.5f, 0.5f, 1f, 16, new Surface { Color = new Vector3(0.9f, 0.84f, 0.7f), Roughness = 0.6f }));

        // Seven candles along the altar, of different heights.
        float[] heights = [0.22f, 0.3f, 0.18f, 0.34f, 0.2f, 0.28f, 0.24f];
        for (int i = 0; i < heights.Length; i++)
        {
            _candles.Add((new Vector3(-10.15f, 1.08f, -37.2f + i * 0.4f), heights[i]));
        }
    }

    /// <summary>
    /// Draws the chapel: the lantern swings with <paramref name="seconds"/>, the view's time, and the flames flicker
    /// with <paramref name="flameSeconds"/>, the game's.
    /// </summary>
    public void Draw(View view, float seconds, float flameSeconds, bool lanternLit)
    {
        // The lantern swings on its chain along the aisle.
        float swing = 0.14f * MathF.Sin(seconds * 1.6f);
        var chain = Matrix4x4.CreateRotationZ(swing);
        var lantern = LanternHook + Vector3.Transform(new Vector3(0f, -LanternChain, 0f), chain);
        var place = chain * Matrix4x4.CreateTranslation(lantern);
        view.Model(ModelKey, _metal, Matrix4x4.CreateScale(0.015f, LanternChain, 0.015f) * Matrix4x4.CreateTranslation(0f, LanternChain / 2f, 0f) * place);
        view.Model(ModelKey + 1, _metal, Matrix4x4.CreateScale(0.28f, 0.06f, 0.28f) * Matrix4x4.CreateTranslation(0f, -0.03f, 0f) * place);
        view.Model(ModelKey + 2, _metal, Matrix4x4.CreateScale(0.24f, 0.03f, 0.24f) * Matrix4x4.CreateTranslation(0f, -0.48f, 0f) * place);
        int post = 0;
        foreach (var (px, pz) in new[] { (-0.11f, -0.11f), (0.11f, -0.11f), (-0.11f, 0.11f), (0.11f, 0.11f) })
        {
            view.Model(ModelKey + 3 + post++, _metal, Matrix4x4.CreateScale(0.02f, 0.42f, 0.02f) * Matrix4x4.CreateTranslation(px, -0.27f, pz) * place);
        }

        view.Model(ModelKey + 10, _wax, Matrix4x4.CreateScale(0.05f, 0.12f, 0.05f) * Matrix4x4.CreateTranslation(0f, -0.405f, 0f) * place);
        var wick = Vector3.Transform(new Vector3(0f, -0.335f, 0f), place);
        if (lanternLit)
        {
            float lanternFlicker = 1f + 0.08f * MathF.Sin(flameSeconds * 9f) + 0.05f * MathF.Sin(flameSeconds * 23f);
            view.Lights.Point(LanternKey, new PointLight(wick + new Vector3(0f, 0.05f, 0f), new Vector3(1.8f, 1.05f, 0.4f) * lanternFlicker, 9f) { Radius = 0.04f, Mode = LightMode.Mixed });
            DrawFlame(view, FlameKey, wick, flameSeconds);
        }

        DrawCandles(view, flameSeconds);
    }

    /// <summary>
    /// A row of candles on the altar, each flickering on its own, with no real-time shadows. They overlap each other, so
    /// only the first three find a channel in the bake's shadow mask, which shades the building from them; the other
    /// four have no shadows at all.
    /// </summary>
    private void DrawCandles(View view, float flameSeconds)
    {
        for (int i = 0; i < _candles.Count; i++)
        {
            var (foot, height) = _candles[i];
            view.Model(ModelKey + 20 + i, _wax, Matrix4x4.CreateScale(0.045f, height, 0.045f) * Matrix4x4.CreateTranslation(foot + new Vector3(0f, height / 2f, 0f)));
            var tip = foot + new Vector3(0f, height + 0.012f, 0f);
            float flicker = 1f + 0.15f * MathF.Sin(flameSeconds * 13f + i * 1.7f) + 0.08f * MathF.Sin(flameSeconds * 29f + i);
            view.Lights.Point(CandleKey + i, new PointLight(tip + new Vector3(0f, 0.03f, 0f), new Vector3(0.4f, 0.19f, 0.055f) * flicker, 3f) { Shadows = false, Mode = LightMode.Mixed });
            DrawFlame(view, FlameKey + (i + 1) * 1000, tip, flameSeconds);
        }
    }

    /// <summary>A flame that never goes out: this second's puffs and last second's, each under its own key.</summary>
    private static void DrawFlame(View view, int key, Vector3 at, float flameSeconds)
    {
        int second = (int)flameSeconds;
        float into = flameSeconds - second;
        view.Effects.Particles(key + (second % 500), Flame, at, into);
        view.Effects.Particles(key + ((second + 499) % 500), Flame, at, into + 1f);
    }
}
