using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Ecs;
using Super.Testing;

namespace Reef.Tests;

public class BoidsTests
{
    private const int Ticks = 300;

    [Theory]
    [Seeds(5)]
    public void Fish_stay_in_the_water_at_their_species_speed(int seed)
    {
        using var run = Start(seed);
        run.Invariant("every fish is inside the reef and moving sensibly", Healthy);

        run.Ticks(Ticks);
    }

    [Theory]
    [Seeds(3)]
    public void The_same_seed_replays_exactly(int seed)
    {
        using var first = Start(seed);
        using var second = Start(seed);

        first.Ticks(Ticks);
        second.Ticks(Ticks);

        Assert.Equal(Positions(first.Game), Positions(second.Game));
    }

    [Fact]
    public void Different_seeds_swim_differently()
    {
        using var first = Start(1);
        using var second = Start(2);

        first.Ticks(30);
        second.Ticks(30);

        Assert.NotEqual(Positions(first.Game), Positions(second.Game));
    }

    [Fact]
    public void Schools_pull_together()
    {
        using var run = Start(1);
        float before = AverageGapToSchoolCentre(run.Game);

        run.Ticks(Ticks);

        Assert.True(AverageGapToSchoolCentre(run.Game) < before);
    }

    private static HeadlessRun<ReefGame> Start(int seed) =>
        HeadlessGame.Create<ReefGame>(seed, services =>
        {
            services.AddSuperEcs();
            services.AddSingleton<ReefSettings>(new SmallReef());
        });

    private static bool Healthy(ReefGame game)
    {
        var settings = game.Settings;
        foreach (var row in game.World.Query<Position, Velocity, Fish>())
        {
            var position = row.First.Value;
            var species = settings.Species[row.Third.Species];
            float speed = row.Second.Value.Length();
            float sideways = new Vector2(position.X, position.Z).Length();
            bool inside = sideways <= settings.Radius + 0.01f
                && position.Y >= Seabed.Height(position.X, position.Z)
                && position.Y <= settings.SurfaceHeight;
            bool moving = speed >= species.MinSpeed - 0.01f && speed <= species.MaxSpeed + 0.01f;
            if (!inside || !moving)
            {
                return false;
            }
        }

        return true;
    }

    private static List<Vector3> Positions(ReefGame game)
    {
        var positions = new List<Vector3>();
        foreach (var row in game.World.Query<Position>())
        {
            positions.Add(row.First.Value);
        }

        return positions;
    }

    private static float AverageGapToSchoolCentre(ReefGame game)
    {
        var sums = new Dictionary<int, (Vector3 Sum, int Count)>();
        foreach (var row in game.World.Query<Position, Fish>())
        {
            sums.TryGetValue(row.Second.School, out var school);
            sums[row.Second.School] = (school.Sum + row.First.Value, school.Count + 1);
        }

        float gaps = 0f;
        int fish = 0;
        foreach (var row in game.World.Query<Position, Fish>())
        {
            var school = sums[row.Second.School];
            gaps += Vector3.Distance(row.First.Value, school.Sum / school.Count);
            fish++;
        }

        return gaps / fish;
    }

    private sealed class SmallReef : ReefSettings
    {
        public override IReadOnlyList<Species> Species { get; } =
        [
            new("reef/tilapia.rmodel", 0.5f, 300, 3, 1.6f, 3.2f, 0.7f, 2.5f),
            new("reef/tuna.rmodel", 1.8f, 30, 1, 4.5f, 7f, 2.5f, 5f),
        ];

        public override int CoralClusters => 2;
    }
}
