using System.Numerics;
using Super;
using Super.Ecs;

namespace Reef;

/// <summary>
/// Schooling. Each fish looks only at nearby fish: it keeps its distance, matches their
/// heading and drifts towards their middle. A grid finds the neighbours, so the cost
/// grows with the number of fish rather than with its square. The grid and its arrays
/// are allocated once. Fish decide on all cores from the same snapshot, then move in
/// order, so the result doesn't depend on how the work was shared out.
/// </summary>
internal sealed class Boids
{
    public const float NeighbourCell = 5f;

    private const int MaxNeighbours = 14;
    private const float SeparationWeight = 3.2f;
    private const float AlignmentWeight = 1.3f;
    private const float CohesionWeight = 0.9f;
    private const float TargetWeight = 0.35f;
    private const float BoundsWeight = 2.5f;
    private const float GroundClearance = 2.5f;
    private const float SurfaceClearance = 3f;
    private const float MaxTurn = 7f; // Metres per second per second.
    private const float TargetReached = 8f;
    private const int FishPerBatch = 256;
    public const float StrokesPerMetre = 2.4f;

    private readonly World _world;
    private readonly ReefSettings _settings;
    private readonly RandomStream _random;
    private readonly Vector3[] _schoolTargets;
    private readonly Vector3[] _positions;
    private readonly Vector3[] _velocities;
    private readonly Vector3[] _pushes;
    private readonly int[] _schools;
    private readonly int[] _species;
    private readonly int[] _nextInCell;
    private readonly int[] _firstInCell;
    private readonly int _columns;
    private readonly int _layers;
    private readonly Action<int> _steerBatch;
    private int _fishThisTick;

    public Boids(World world, ReefSettings settings, RandomStream random, int schoolCount)
    {
        _world = world;
        _settings = settings;
        _random = random;
        _schoolTargets = new Vector3[schoolCount];
        for (int i = 0; i < schoolCount; i++)
        {
            _schoolTargets[i] = PickTarget();
        }

        int fish = settings.Species.Sum(species => species.Count);
        _positions = new Vector3[fish];
        _velocities = new Vector3[fish];
        _pushes = new Vector3[fish];
        _schools = new int[fish];
        _species = new int[fish];
        _nextInCell = new int[fish];

        _columns = (int)(settings.Radius * 2f / NeighbourCell) + 3;
        _layers = (int)(settings.SurfaceHeight / NeighbourCell) + 3;
        _firstInCell = new int[_columns * _columns * _layers];
        _steerBatch = SteerBatch;
    }

    public void Update(float seconds)
    {
        Array.Fill(_firstInCell, -1);
        int count = 0;
        foreach (var row in _world.Query<Transform, Velocity, Fish>())
        {
            _positions[count] = row.First.Position;
            _velocities[count] = row.Second.Value;
            _schools[count] = row.Third.School;
            _species[count] = row.Third.Species;
            int cell = CellOf(row.First.Position);
            _nextInCell[count] = _firstInCell[cell];
            _firstInCell[cell] = count;
            count++;
        }

        MoveTargets();

        _fishThisTick = count;
        Parallel.For(0, (count + FishPerBatch - 1) / FishPerBatch, _steerBatch);

        int index = 0;
        foreach (var row in _world.Query<Transform, Velocity, Fish, Swim>())
        {
            var species = _settings.Species[row.Third.Species];
            var push = _pushes[index];
            float length = push.Length();
            if (length > MaxTurn)
            {
                push *= MaxTurn / length;
            }

            var velocity = row.Second.Value + push * seconds;
            float speed = velocity.Length();
            float limited = Math.Clamp(speed, species.MinSpeed, species.MaxSpeed);
            velocity = speed > 0f ? velocity * (limited / speed) : Vector3.UnitZ * limited;

            row.Second.Value = velocity;
            row.First = Transform.Facing(Keep(row.First.Position + velocity * seconds), velocity / limited, species.Length);
            row.Fourth.Phase += limited * seconds * StrokesPerMetre / species.Length;
            index++;
        }
    }

    private void SteerBatch(int batch)
    {
        int end = Math.Min((batch + 1) * FishPerBatch, _fishThisTick);
        for (int fish = batch * FishPerBatch; fish < end; fish++)
        {
            _pushes[fish] = Steer(fish, _settings.Species[_species[fish]]);
        }
    }

    private Vector3 Steer(int fish, Species species)
    {
        var position = _positions[fish];
        int school = _schools[fish];
        var away = Vector3.Zero;
        var heading = Vector3.Zero;
        var middle = Vector3.Zero;
        int schoolmates = 0;
        int seen = 0;
        float sightSquared = species.Sight * species.Sight;
        float personalSquared = species.Personal * species.Personal;

        int cellX = CellIndex(position.X + _settings.Radius);
        int cellY = CellIndex(position.Y);
        int cellZ = CellIndex(position.Z + _settings.Radius);
        for (int y = Math.Max(cellY - 1, 0); y <= Math.Min(cellY + 1, _layers - 1); y++)
        {
            for (int z = Math.Max(cellZ - 1, 0); z <= Math.Min(cellZ + 1, _columns - 1); z++)
            {
                for (int x = Math.Max(cellX - 1, 0); x <= Math.Min(cellX + 1, _columns - 1); x++)
                {
                    for (int other = _firstInCell[(y * _columns + z) * _columns + x]; other >= 0 && seen < MaxNeighbours; other = _nextInCell[other])
                    {
                        if (other == fish)
                        {
                            continue;
                        }

                        var offset = position - _positions[other];
                        float squared = offset.LengthSquared();
                        if (squared >= sightSquared || squared == 0f)
                        {
                            continue;
                        }

                        seen++;
                        if (squared < personalSquared)
                        {
                            away += offset / squared;
                        }

                        if (_schools[other] == school)
                        {
                            heading += _velocities[other];
                            middle += _positions[other];
                            schoolmates++;
                        }
                    }
                }
            }
        }

        var push = away * SeparationWeight;
        if (schoolmates > 0)
        {
            push += (heading / schoolmates - _velocities[fish]) * AlignmentWeight;
            push += (middle / schoolmates - position) * CohesionWeight;
        }

        var toTarget = _schoolTargets[school] - position;
        push += SafeNormalize(toTarget) * species.MaxSpeed * TargetWeight;
        return push + AvoidEdges(position);
    }

    private Vector3 AvoidEdges(Vector3 position)
    {
        var push = Vector3.Zero;
        float sideways = MathF.Sqrt(position.X * position.X + position.Z * position.Z);
        float outside = sideways - (_settings.Radius - 8f);
        if (outside > 0f)
        {
            push -= new Vector3(position.X, 0f, position.Z) / sideways * outside * BoundsWeight;
        }

        // Most fish are too high to need the seabed's exact height.
        if (position.Y < Seabed.Highest + GroundClearance)
        {
            float ground = Seabed.Height(position.X, position.Z) + GroundClearance;
            if (position.Y < ground)
            {
                push.Y += (ground - position.Y) * BoundsWeight * 2f;
            }
        }

        float ceiling = _settings.SurfaceHeight - SurfaceClearance;
        if (position.Y > ceiling)
        {
            push.Y -= (position.Y - ceiling) * BoundsWeight * 2f;
        }

        return push;
    }

    /// <summary>The last resort, for a fish that steering didn't turn in time.</summary>
    private Vector3 Keep(Vector3 position)
    {
        float sideways = MathF.Sqrt(position.X * position.X + position.Z * position.Z);
        if (sideways > _settings.Radius)
        {
            position.X *= _settings.Radius / sideways;
            position.Z *= _settings.Radius / sideways;
        }

        if (position.Y < Seabed.Highest + 0.3f)
        {
            position.Y = MathF.Max(position.Y, Seabed.Height(position.X, position.Z) + 0.3f);
        }

        position.Y = MathF.Min(position.Y, _settings.SurfaceHeight - 0.5f);
        return position;
    }

    private void MoveTargets()
    {
        // A school heads somewhere new on reaching its goal, using the first member as its stand-in.
        Span<bool> checkedSchool = stackalloc bool[_schoolTargets.Length];
        foreach (var row in _world.Query<Transform, Fish>())
        {
            int school = row.Second.School;
            if (checkedSchool[school])
            {
                continue;
            }

            checkedSchool[school] = true;
            if (Vector3.DistanceSquared(row.First.Position, _schoolTargets[school]) < TargetReached * TargetReached)
            {
                _schoolTargets[school] = PickTarget();
            }
        }
    }

    private Vector3 PickTarget()
    {
        var spot = _random.InsideCircle(_settings.Radius * 0.8f);
        float ground = Seabed.Height(spot.X, spot.Y);
        return new Vector3(spot.X, _random.Between(ground + 4f, _settings.SurfaceHeight - 5f), spot.Y);
    }

    private int CellOf(Vector3 position) =>
        (CellIndex(position.Y) * _columns + CellIndex(position.Z + _settings.Radius)) * _columns + CellIndex(position.X + _settings.Radius);

    // The first cell is a margin, so neighbours just outside the area are safe to reach.
    private int CellIndex(float metres) => Math.Max((int)(metres / NeighbourCell) + 1, 0);

    private static Vector3 SafeNormalize(Vector3 value)
    {
        float length = value.Length();
        return length > 0.001f ? value / length : Vector3.Zero;
    }
}
