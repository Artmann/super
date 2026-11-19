using System.Numerics;
using Examples;
using Super;

namespace Crypt;

/// <summary>Candles standing on the floor in clusters, each its own height and width, each with its flame and light.</summary>
internal sealed class LooseCandles(World world)
{
    private static readonly Vector3 Wax = new(0.85f, 0.78f, 0.62f);

    private int _count;

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

    /// <summary>A candle of its own height and width at <paramref name="foot"/>, burning with a flame and a little light that flickers.</summary>
    private void Add(Random random, Vector3 foot)
    {
        float height = 0.06f + (0.22f * MathF.Pow((float)random.NextDouble(), 1.5f));
        float width = 0.035f + (0.035f * (float)random.NextDouble());
        var wick = foot + new Vector3(0f, height + 0.012f, 0f);
        var color = new Vector3(0.16f, 0.075f, 0.022f);

        world.Spawn(new Transform(foot + new Vector3(0f, height * 0.5f, 0f), Quaternion.Identity, new Vector3(width, height, width)), new Model(ModelRef.Cylinder, Wax), new Static());
        world.Spawn(new PointLight(wick + new Vector3(0f, 0.03f, 0f), color, 1.6f) { Shadows = false }, new Flicker(color, 0.15f, 13f, _count));
        world.Spawn(Transform.At(wick), new Emitter(CryptFire.CandleFlame));
        _count++;
    }
}
