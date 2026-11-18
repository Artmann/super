using System.Numerics;
using Rendition;
using Super;

namespace Crypt;

/// <summary>Candles standing on the floor in clusters, each its own height and width, each with its flame and light.</summary>
internal sealed class LooseCandles
{
    private const int CandleKey = 1000;
    private const int FlameKey = 200_000;

    private readonly List<(Vector3 Foot, float Height, float Width)> _candles = [];

    /// <summary>Scatters <paramref name="count"/> candles over a box <paramref name="half"/> wide each way round <paramref name="middle"/>.</summary>
    public void Scatter(Random random, Vector3 middle, Vector2 half, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var at = middle + new Vector3(((float)random.NextDouble() * 2f - 1f) * half.X, 0f, ((float)random.NextDouble() * 2f - 1f) * half.Y);
            Add(random, at);
        }
    }

    /// <summary>Puts <paramref name="count"/> candles in a ring from <paramref name="inner"/> to <paramref name="outer"/> round <paramref name="middle"/>.</summary>
    public void Ring(Random random, Vector3 middle, float inner, float outer, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float turn = ((i + (float)random.NextDouble() * 0.6f) / count) * MathF.Tau;
            float reach = inner + ((float)random.NextDouble() * (outer - inner));
            Add(random, middle + new Vector3(MathF.Cos(turn) * reach, 0f, MathF.Sin(turn) * reach));
        }
    }

    /// <summary>Draws the candles, their flames flickering with <paramref name="seconds"/>.</summary>
    public void Draw(View view, float seconds)
    {
        var wax = new Vector3(0.85f, 0.78f, 0.62f);
        for (int i = 0; i < _candles.Count; i++)
        {
            var (foot, height, width) = _candles[i];
            view.Model(CandleKey + i, ModelRef.Cylinder, Matrix4x4.CreateScale(width, height, width) * Matrix4x4.CreateTranslation(foot + new Vector3(0f, height * 0.5f, 0f)), new ModelOptions { Color = wax, Static = true });
            var wick = foot + new Vector3(0f, height + 0.012f, 0f);
            float flicker = 1f + (0.15f * MathF.Sin(seconds * 13f + i));
            view.Lights.Point(CandleKey + i, new PointLight(wick + new Vector3(0f, 0.03f, 0f), new Vector3(0.16f, 0.075f, 0.022f) * flicker, 1.6f) { Shadows = false });
            CryptFire.Draw(view, FlameKey + (i * 1000), CryptFire.CandleFlame, wick, seconds);
        }
    }

    private void Add(Random random, Vector3 foot)
    {
        float height = 0.06f + (0.22f * MathF.Pow((float)random.NextDouble(), 1.5f));
        float width = 0.035f + (0.035f * (float)random.NextDouble());
        _candles.Add((foot, height, width));
    }
}
