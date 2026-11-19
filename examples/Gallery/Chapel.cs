using System.Numerics;
using Examples;
using Rendition;
using Super;

namespace Gallery;

/// <summary>
/// What moves and flickers in the chapel: a lantern swinging on its chain over the aisle, and candles on the altar. The
/// lantern's chain, frame, candle, light and flame all hang from one entity at the hook, which <see cref="Swing"/> turns.
/// </summary>
internal sealed class Chapel
{
    /// <summary>The chapel lantern hangs from a hook in the roof, over the aisle.</summary>
    private static readonly Vector3 LanternHook = new(-6.2f, 8f, -36f);
    private const float LanternChain = 4.4f;

    /// <summary>The lantern's flame, and where its wick is under the hook.</summary>
    private static readonly Vector3 LanternWick = new(0f, -LanternChain - 0.335f, 0f);

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

    private static readonly Vector3 LanternColor = new(1.8f, 1.05f, 0.4f);

    private readonly World _world;
    private readonly Entity _pivot;
    private readonly Entity _lanternLight;
    private readonly Entity _lanternFlame;
    private bool _lit = true;

    /// <summary>Hangs the lantern, its metal in <paramref name="shade"/>, and stands the candles on the altar.</summary>
    public Chapel(World world, Content content, Surface shade)
    {
        _world = world;
        var metal = content.Mesh(Shapes.Box(Vector3.One, shade));
        var wax = content.Mesh(Shapes.Cylinder(0.5f, 0.5f, 1f, 16, new Surface { Color = new Vector3(0.9f, 0.84f, 0.7f), Roughness = 0.6f }));

        _pivot = world.Spawn(Transform.At(LanternHook));
        Hang(metal, new Vector3(0f, -LanternChain / 2f, 0f), new Vector3(0.015f, LanternChain, 0.015f));
        Hang(metal, new Vector3(0f, -LanternChain - 0.03f, 0f), new Vector3(0.28f, 0.06f, 0.28f));
        Hang(metal, new Vector3(0f, -LanternChain - 0.48f, 0f), new Vector3(0.24f, 0.03f, 0.24f));
        foreach (var (px, pz) in new[] { (-0.11f, -0.11f), (0.11f, -0.11f), (-0.11f, 0.11f), (0.11f, 0.11f) })
        {
            Hang(metal, new Vector3(px, -LanternChain - 0.27f, pz), new Vector3(0.02f, 0.42f, 0.02f));
        }

        Hang(wax, new Vector3(0f, -LanternChain - 0.405f, 0f), new Vector3(0.05f, 0.12f, 0.05f));
        _lanternLight = world.Spawn(Transform.Identity, LanternLight(), LanternFlicker(), new Parent(_pivot));
        _lanternFlame = world.Spawn(Transform.At(LanternWick), new Emitter(Flame), new Parent(_pivot));

        Candles(world, wax);
        Swing(0f);
    }

    /// <summary>Swings the lantern on its chain along the aisle to where it is at <paramref name="seconds"/>.</summary>
    public void Swing(float seconds)
    {
        _world.Get<Transform>(_pivot).Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.14f * MathF.Sin(seconds * 1.6f));
    }

    /// <summary>Puts the lantern out, or lights it again: its light and its flame go and come back.</summary>
    public void Light(bool lit)
    {
        if (lit == _lit)
        {
            return;
        }

        _lit = lit;
        if (lit)
        {
            _world.Add(_lanternLight, LanternLight());
            _world.Add(_lanternLight, LanternFlicker());
            _world.Add(_lanternFlame, new Emitter(Flame));
        }
        else
        {
            _world.Remove<PointLight>(_lanternLight);
            _world.Remove<Flicker>(_lanternLight);
            _world.Remove<Emitter>(_lanternFlame);
        }
    }

    private static PointLight LanternLight() =>
        new(LanternWick + new Vector3(0f, 0.05f, 0f), LanternColor, 9f) { Radius = 0.04f, Mode = LightMode.Mixed };

    private static Flicker LanternFlicker() => new(LanternColor, 0.08f, 9f, 0f, Ripple: 0.05f, RippleSpeed: 23f);

    private void Hang(ModelRef mesh, Vector3 at, Vector3 scale) =>
        _world.Spawn(new Transform(at, Quaternion.Identity, scale), new Model(mesh), new Parent(_pivot));

    /// <summary>
    /// A row of seven candles on the altar, of different heights, each flickering on its own, with no real-time
    /// shadows. They overlap each other, so only the first three find a channel in the bake's shadow mask, which shades
    /// the building from them; the other four have no shadows at all.
    /// </summary>
    private static void Candles(World world, ModelRef wax)
    {
        float[] heights = [0.22f, 0.3f, 0.18f, 0.34f, 0.2f, 0.28f, 0.24f];
        var color = new Vector3(0.4f, 0.19f, 0.055f);
        for (int i = 0; i < heights.Length; i++)
        {
            var foot = new Vector3(-10.15f, 1.08f, -37.2f + i * 0.4f);
            float height = heights[i];
            world.Spawn(new Transform(foot + new Vector3(0f, height / 2f, 0f), Quaternion.Identity, new Vector3(0.045f, height, 0.045f)), new Model(wax));
            var tip = foot + new Vector3(0f, height + 0.012f, 0f);
            world.Spawn(
                new PointLight(tip + new Vector3(0f, 0.03f, 0f), color, 3f) { Shadows = false, Mode = LightMode.Mixed },
                new Flicker(color, 0.15f, 13f, i * 1.7f, Ripple: 0.08f, RippleSpeed: 29f, RipplePhase: i));
            world.Spawn(Transform.At(tip), new Emitter(Flame));
        }
    }
}
