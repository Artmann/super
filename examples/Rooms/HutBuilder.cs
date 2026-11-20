using System.Numerics;
using Super;

namespace Rooms;

/// <summary>The huts the player builds outside, each rising out of the ground beside the player.</summary>
internal sealed class HutBuilder(World world, Scenes scenes, Catalog<Motion> motions)
{
    /// <summary>How high a hut's middle stands once it's up.</summary>
    public const float Height = 0.1f;

    /// <summary>How far down a hut's middle starts.</summary>
    public const float Buried = -1.3f;

    private static readonly Vector3 HutColor = new(0.75f, 0.3f, 0.3f);
    private static readonly Vector3 HutSpot = new(1.8f, 0f, 0f);

    private readonly List<Entity> _risen = [];

    /// <summary>
    /// Puts a hut beside <paramref name="player"/> when <paramref name="build"/> is true, and raises every hut that's
    /// still on its way up.
    /// </summary>
    public void Update(GameTime time, Vector3 player, bool build)
    {
        if (build)
        {
            var rise = motions["rise"];
            var spot = player + HutSpot;
            scenes.Current!.Spawn(
                Transform.At(spot with { Y = Buried }, 1.2f),
                new Model(ModelRef.Cube, HutColor),
                new Hut(),
                new Rising(Tween.Between(Buried, Height, rise.Seconds).Ease(rise.Curve)));
        }

        foreach (var row in world.Query<Transform, Rising>())
        {
            ref var height = ref row.Second.Height;
            height.Update(time);
            row.First.Position.Y = height.Value;
            if (height.Completed)
            {
                _risen.Add(row.Entity);
            }
        }

        // A hut that's up has nothing left to do, so it stops being asked.
        foreach (var hut in _risen)
        {
            world.Remove<Rising>(hut);
        }

        _risen.Clear();
    }

    /// <summary>Where the huts are now, built or still rising.</summary>
    public List<Vector3> Positions()
    {
        var positions = new List<Vector3>();
        foreach (var row in world.Query<Transform, Hut>())
        {
            positions.Add(row.First.Position);
        }

        return positions;
    }
}
