using System.Numerics;
using Super;
using Super.Ecs;

namespace Reef;

/// <summary>Where each school is heading: a spot in the open water, picked again once the school gets there.</summary>
internal sealed class SchoolTargets
{
    private const float TargetReached = 8f;

    private readonly ReefSettings _settings;
    private readonly RandomStream _random;
    private readonly Vector3[] _targets;

    public SchoolTargets(ReefSettings settings, RandomStream random, int schoolCount)
    {
        _settings = settings;
        _random = random;
        _targets = new Vector3[schoolCount];
        for (int i = 0; i < schoolCount; i++)
        {
            _targets[i] = PickTarget();
        }
    }

    /// <summary>Which way <paramref name="school"/>'s target is from <paramref name="position"/>, as a unit vector, or zero when it's there.</summary>
    public Vector3 Toward(int school, Vector3 position)
    {
        var toTarget = _targets[school] - position;
        float length = toTarget.Length();
        return length > 0.001f ? toTarget / length : Vector3.Zero;
    }

    /// <summary>A school heads somewhere new on reaching its goal, using the first member as its stand-in.</summary>
    public void Move(World world)
    {
        Span<bool> checkedSchool = stackalloc bool[_targets.Length];
        foreach (var row in world.Query<Transform, Fish>())
        {
            int school = row.Second.School;
            if (checkedSchool[school])
            {
                continue;
            }

            checkedSchool[school] = true;
            if (Vector3.DistanceSquared(row.First.Position, _targets[school]) < TargetReached * TargetReached)
            {
                _targets[school] = PickTarget();
            }
        }
    }

    private Vector3 PickTarget()
    {
        var spot = _random.InsideCircle(_settings.Radius * 0.8f);
        float ground = Seabed.Height(spot.X, spot.Y);
        return new Vector3(spot.X, _random.Between(ground + 4f, _settings.SurfaceHeight - 5f), spot.Y);
    }
}
