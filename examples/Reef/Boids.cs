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
    private const int MaxNeighbours = 14;
    private const float SeparationWeight = 3.2f;
    private const float AlignmentWeight = 1.3f;
    private const float CohesionWeight = 0.9f;
    private const float TargetWeight = 0.35f;
    private const float MaxTurn = 7f; // Metres per second per second.
    private const int FishPerBatch = 256;
    public const float StrokesPerMetre = 2.4f;

    private readonly World _world;
    private readonly ReefSettings _settings;
    private readonly SchoolTargets _targets;
    private readonly ReefBounds _bounds;
    private readonly NeighbourGrid _grid;
    private readonly Vector3[] _positions;
    private readonly Vector3[] _velocities;
    private readonly Vector3[] _pushes;
    private readonly int[] _schools;
    private readonly int[] _species;
    private readonly Action<int> _steerBatch;
    private int _fishThisTick;

    public Boids(World world, ReefSettings settings, RandomStream random, int schoolCount)
    {
        _world = world;
        _settings = settings;
        _targets = new SchoolTargets(settings, random, schoolCount);
        _bounds = new ReefBounds(settings);

        int fish = settings.Species.Sum(species => species.Count);
        _positions = new Vector3[fish];
        _velocities = new Vector3[fish];
        _pushes = new Vector3[fish];
        _schools = new int[fish];
        _species = new int[fish];
        _grid = new NeighbourGrid(settings.Radius, settings.SurfaceHeight, fish);
        _steerBatch = SteerBatch;
    }

    public void Update(float seconds)
    {
        int count = Gather();
        _targets.Move(_world);

        _fishThisTick = count;
        Parallel.For(0, (count + FishPerBatch - 1) / FishPerBatch, _steerBatch);
        MoveFish(seconds);
    }

    /// <summary>Copies every fish into the arrays the steering reads, and into the grid, and gives how many there are.</summary>
    private int Gather()
    {
        _grid.Clear();
        int count = 0;
        foreach (var row in _world.Query<Transform, Velocity, Fish>())
        {
            _positions[count] = row.First.Position;
            _velocities[count] = row.Second.Value;
            _schools[count] = row.Third.School;
            _species[count] = row.Third.Species;
            _grid.Add(count, row.First.Position);
            count++;
        }

        return count;
    }

    /// <summary>Turns each fish by its push, within its species' speeds, and moves it on, in order.</summary>
    private void MoveFish(float seconds)
    {
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
            row.First = Transform.Facing(_bounds.Keep(row.First.Position + velocity * seconds), velocity / limited, species.Length);
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
        var flock = Look(fish, species);

        var push = flock.Away * SeparationWeight;
        if (flock.Schoolmates > 0)
        {
            push += (flock.Heading / flock.Schoolmates - _velocities[fish]) * AlignmentWeight;
            push += (flock.Middle / flock.Schoolmates - position) * CohesionWeight;
        }

        push += _targets.Toward(_schools[fish], position) * species.MaxSpeed * TargetWeight;
        return push + _bounds.AvoidEdges(position);
    }

    /// <summary>What a fish sees of the others in its cell and the cells round it, up to <see cref="MaxNeighbours"/>.</summary>
    private Flock Look(int fish, Species species)
    {
        var flock = new Flock();
        Span<int> cells = stackalloc int[27];
        int count = _grid.CellsAround(_positions[fish], cells);
        for (int i = 0; i < count; i++)
        {
            LookInCell(fish, cells[i], species, ref flock);
        }

        return flock;
    }

    private void LookInCell(int fish, int cell, Species species, ref Flock flock)
    {
        var position = _positions[fish];
        float sightSquared = species.Sight * species.Sight;
        float personalSquared = species.Personal * species.Personal;
        for (int other = _grid.First(cell); other >= 0 && flock.Seen < MaxNeighbours; other = _grid.Next(other))
        {
            var offset = position - _positions[other];
            float squared = offset.LengthSquared();
            if (other == fish || squared >= sightSquared || squared == 0f)
            {
                continue;
            }

            flock.Seen++;
            if (squared < personalSquared)
            {
                flock.Away += offset / squared;
            }

            if (_schools[other] == _schools[fish])
            {
                flock.Heading += _velocities[other];
                flock.Middle += _positions[other];
                flock.Schoolmates++;
            }
        }
    }

    /// <summary>The sums a fish steers by: away from those too close, and towards its schoolmates' heading and middle.</summary>
    private struct Flock
    {
        public Vector3 Away;
        public Vector3 Heading;
        public Vector3 Middle;
        public int Schoolmates;
        public int Seen;
    }
}
