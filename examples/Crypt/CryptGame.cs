using System.Numerics;
using Rendition;
using Super;

namespace Crypt;

/// <summary>
/// A vault lit only by fire: four torches on the pillars throwing shadows, a lantern's beam
/// from above, and rows of candles on the floor, 250 lights in all. The flames are particles
/// that glow, and a pane of coloured glass catches the torchlight.
/// </summary>
public sealed class CryptGame : IGame
{
    private const int FloorKey = 1;
    private const int WallKey = 10;
    private const int PillarKey = 100;
    private const int TorchKey = 200;
    private const int CandleKey = 1000;
    private const int GlassKey = 5000;
    private const int ChestKey = 5100;
    private const int LanternKey = 5200;
    private const int FlameKey = 10_000;
    private const int CandleRows = 6;
    private const int CandlesPerRow = 41;

    /// <summary>
    /// A flame's licks: small glowing puffs rising from the wick, warm yellow going to deep
    /// orange as they thin, born all through a second so one second's worth runs on into the next.
    /// </summary>
    public static readonly ParticleEmitter Flame = new ParticleEmitter(count: 14, lifetime: 0.45f)
        .Over(1f)
        .FromSphere(0.012f)
        .Velocity(new Vector3(0f, 0.25f, 0f))
        .Scatter(0.02f)
        .Gravity(new Vector3(0f, 0.6f, 0f))
        .Size(0.035f, 0.01f)
        .Color(new Vector3(1f, 0.8f, 0.4f), new Vector3(1f, 0.35f, 0.08f))
        .Glow(6f)
        .Fade(0.1f, 0.5f);

    /// <summary>A torch's bigger fire, the same flame scaled up.</summary>
    public static readonly ParticleEmitter TorchFire = new ParticleEmitter(count: 30, lifetime: 0.6f)
        .Over(1f)
        .FromSphere(0.05f)
        .Velocity(new Vector3(0f, 0.5f, 0f))
        .Scatter(0.08f)
        .Gravity(new Vector3(0f, 1f, 0f))
        .Size(0.16f, 0.04f)
        .Color(new Vector3(1f, 0.75f, 0.35f), new Vector3(0.9f, 0.25f, 0.05f))
        .Glow(5f)
        .Fade(0.1f, 0.5f)
        .Spin(1f);

    private static readonly ParticleEffect CandleFlame = new ParticleEffect().Add(Flame);
    private static readonly ParticleEffect Torch = new ParticleEffect().Add(TorchFire);

    private static readonly Vector3[] Pillars =
    [
        new(-4f, 0f, -3f), new(4f, 0f, -3f), new(-4f, 0f, 3f), new(4f, 0f, 3f),
    ];

    private float _seconds;

    public void Load()
    {
    }

    public void Update(GameTime time) => _seconds = time.Tick / (float)time.TicksPerSecond;

    public void Draw(View view)
    {
        view.Camera(new Vector3(0f, 6.5f, 12.5f), new Vector3(0f, 0.6f, 0f), MathF.PI / 4f);
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.02f, 0.025f, 0.04f),
            Horizon = new Vector3(0.02f, 0.02f, 0.03f),
            Ground = new Vector3(0.01f, 0.01f, 0.01f),
            Brightness = 0.15f,
        });
        view.Finish(new PostSettings { Bloom = 0.05f, Vignette = 0.3f });

        var stone = new Vector3(0.45f, 0.42f, 0.38f);
        view.Model(FloorKey, ModelRef.Cube, Matrix4x4.CreateScale(14f, 0.2f, 12f) * Matrix4x4.CreateTranslation(0f, -0.1f, 0f), stone);
        view.Model(WallKey, ModelRef.Cube, Matrix4x4.CreateScale(14f, 5f, 0.4f) * Matrix4x4.CreateTranslation(0f, 2.5f, -6f), stone);
        view.Model(WallKey + 1, ModelRef.Cube, Matrix4x4.CreateScale(0.4f, 5f, 12f) * Matrix4x4.CreateTranslation(-7f, 2.5f, 0f), stone);
        view.Model(WallKey + 2, ModelRef.Cube, Matrix4x4.CreateScale(0.4f, 5f, 12f) * Matrix4x4.CreateTranslation(7f, 2.5f, 0f), stone);

        for (int i = 0; i < Pillars.Length; i++)
        {
            var foot = Pillars[i];
            view.Model(PillarKey + i, ModelRef.Cylinder, Matrix4x4.CreateScale(0.7f, 4.5f, 0.7f) * Matrix4x4.CreateTranslation(foot + new Vector3(0f, 2.25f, 0f)), stone);

            // A torch on the pillar's side towards the middle, flickering as fire does.
            var side = Vector3.Normalize(new Vector3(-foot.X, 0f, -foot.Z)) * 0.45f;
            var bracket = foot + side + new Vector3(0f, 2.1f, 0f);
            view.Model(TorchKey + i, ModelRef.Cylinder, Matrix4x4.CreateScale(0.08f, 0.5f, 0.08f) * Matrix4x4.CreateTranslation(bracket), new Vector3(0.25f, 0.15f, 0.08f));
            var fire = bracket + new Vector3(0f, 0.32f, 0f);
            float flicker = 1f + (0.12f * MathF.Sin(_seconds * 11f + i * 2f)) + (0.08f * MathF.Sin(_seconds * 17f + i));
            view.PointLight(TorchKey + i, fire + new Vector3(0f, 0.1f, 0f), new Vector3(3.2f, 1.5f, 0.5f) * flicker, range: 9f);
            DrawFire(view, FlameKey + i * 1000, Torch, fire);
        }

        // A lantern hung high, its beam falling on a chest between the pillars.
        var lantern = new Vector3(0f, 4.2f, 0.5f);
        view.Model(LanternKey, ModelRef.Cube, Matrix4x4.CreateScale(0.25f) * Matrix4x4.CreateTranslation(lantern), new Vector3(0.1f, 0.1f, 0.1f));
        view.SpotLight(LanternKey, lantern - new Vector3(0f, 0.15f, 0f), new Vector3(0f, -1f, -0.15f), new Vector3(6f, 5.5f, 4.5f), range: 7f, innerAngle: 0.25f, outerAngle: 0.4f);
        view.Model(ChestKey, ModelRef.Cube, Matrix4x4.CreateScale(1.2f, 0.7f, 0.7f) * Matrix4x4.CreateTranslation(0f, 0.35f, 0f), new Vector3(0.4f, 0.25f, 0.12f));

        // Rows of candles before the back wall, each a small light with no shadow.
        for (int row = 0; row < CandleRows; row++)
        {
            for (int c = 0; c < CandlesPerRow; c++)
            {
                int index = (row * CandlesPerRow) + c;
                var foot = new Vector3(-6f + (c * 0.3f), row * 0.08f, -5.4f + (row * 0.25f));
                float height = 0.12f + (0.05f * ((index * 7) % 5) / 4f);
                view.Model(CandleKey + index, ModelRef.Cylinder, Matrix4x4.CreateScale(0.05f, height, 0.05f) * Matrix4x4.CreateTranslation(foot + new Vector3(0f, height * 0.5f, 0f)), new Vector3(0.9f, 0.85f, 0.7f));
                var wick = foot + new Vector3(0f, height + 0.02f, 0f);
                float flicker = 1f + (0.15f * MathF.Sin(_seconds * 13f + index));
                view.PointLight(CandleKey + index, wick + new Vector3(0f, 0.03f, 0f), new Vector3(0.12f, 0.06f, 0.02f) * flicker, range: 1.4f, shadows: false);
                DrawFire(view, FlameKey + 10_000 + (index * 1000), CandleFlame, wick);
            }
        }

        // A pane of red glass the torchlight shines on.
        view.Ghost(GlassKey, ModelRef.Cube, Matrix4x4.CreateScale(1.6f, 2f, 0.05f) * Matrix4x4.CreateTranslation(-2.2f, 1f, 1.5f), new Vector3(0.6f, 0.1f, 0.1f), opacity: 0.35f);
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
