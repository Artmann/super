using System.Numerics;
using Super;

namespace Rooms;

/// <summary>The coins outside: scattered when the player goes out, picked up by walking over them, and counted.</summary>
internal sealed class CoinHunt(World world, States<Screen> screen, Catalog<CoinKind> kinds, RandomStream spots)
{
    /// <summary>How many coins lie outside each time the player goes out.</summary>
    public const int Count = 8;

    /// <summary>The draw key of the flash over the player when a coin is picked up.</summary>
    public const int SparkleKey = -2;

    private const float PickUpWithin = 1f;
    private const float SparkleSeconds = 0.4f;

    private static readonly Vector3 CoinColor = new(1f, 0.8f, 0.1f);

    private long? _lastCoinTick;

    /// <summary>Coins picked up, by their value.</summary>
    public int Total { get; private set; }

    /// <summary>Lays the coins out at random across ground <paramref name="size"/> from the middle each way.</summary>
    public void Scatter(float size)
    {
        for (int i = 0; i < Count; i++)
        {
            float x = spots.Float() * 2f * (size - 1f) - (size - 1f);
            float z = spots.Float() * 2f * (size - 3f) - (size - 5f);
            var coin = ScopedCube.Add(world, screen, Transform.At(new Vector3(x, 0f, z), kinds["gold"].Size), CoinColor);
            world.Add(coin, new Coin());
        }
    }

    /// <summary>Picks up the coins within reach of <paramref name="player"/>.</summary>
    public void Collect(GameTime time, Vector3 player)
    {
        foreach (var row in world.Query<Transform, Coin>())
        {
            if (Vector3.Distance(row.First.Position, player) < PickUpWithin)
            {
                // The coin goes when the loop ends, so the rows being walked never move.
                world.Destroy(row.Entity);
                Total += kinds["gold"].Value;
                _lastCoinTick = time.Tick;
            }
        }
    }

    /// <summary>Where the coins are now.</summary>
    public List<Vector3> Positions()
    {
        var positions = new List<Vector3>();
        foreach (var row in world.Query<Transform, Coin>())
        {
            positions.Add(row.First.Position);
        }

        return positions;
    }

    /// <summary>A flash above <paramref name="player"/>, shrinking as the last coin gets older.</summary>
    public void DrawSparkle(View view, Vector3 player)
    {
        if (_lastCoinTick is not { } tick || view.SecondsSince(tick) >= SparkleSeconds)
        {
            return;
        }

        float size = 0.5f * (1f - view.SecondsSince(tick) / SparkleSeconds);
        var sparkle = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(player + new Vector3(0f, 1.2f, 0f));
        view.Model(SparkleKey, ModelRef.Cube, sparkle, CoinColor);
    }
}
