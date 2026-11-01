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

        // Different seeds already spawn the fish apart, which would make this true at once.
        // Putting the second reef's fish where the first's are leaves only the swimming to differ.
        CopyFishFrom(first.Game, second.Game);
        Assert.Equal(FishPositions(first.Game), FishPositions(second.Game));

        first.Ticks(30);
        second.Ticks(30);

        Assert.NotEqual(FishPositions(first.Game), FishPositions(second.Game));
    }

    [Fact]
    public void A_fish_heading_for_the_edge_is_steered_round_and_not_just_stopped_at_it()
    {
        using var run = Start(1);
        float radius = run.Game.Settings.Radius;
        var swimmer = default(Entity);
        foreach (var row in run.Game.World.Query<Transform, Velocity, Fish>())
        {
            row.First.Position = new Vector3(radius - 4f, 15f, 0f);
            row.Second.Value = new Vector3(3f, 0f, 0f);
            swimmer = row.Entity;
            break;
        }

        float farthest = 0f;
        for (int i = 0; i < 300; i++)
        {
            run.Ticks(1);
            var position = run.Game.World.Get<Transform>(swimmer).Position;
            farthest = MathF.Max(farthest, new Vector2(position.X, position.Z).Length());
        }

        Assert.True(farthest < radius - 1f, $"The fish reached {farthest} of {radius}.");
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
        foreach (var row in game.World.Query<Transform, Velocity, Fish>())
        {
            var position = row.First.Position;
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

    private static void CopyFishFrom(ReefGame source, ReefGame target)
    {
        var fish = new List<(Transform Transform, Velocity Velocity)>();
        foreach (var row in source.World.Query<Transform, Velocity, Fish>())
        {
            fish.Add((row.First, row.Second));
        }

        int index = 0;
        foreach (var row in target.World.Query<Transform, Velocity, Fish>())
        {
            row.First = fish[index].Transform;
            row.Second = fish[index].Velocity;
            index++;
        }
    }

    private static List<Vector3> FishPositions(ReefGame game)
    {
        var positions = new List<Vector3>();
        foreach (var row in game.World.Query<Transform, Fish>())
        {
            positions.Add(row.First.Position);
        }

        return positions;
    }

    private static List<Vector3> Positions(ReefGame game)
    {
        var positions = new List<Vector3>();
        foreach (var row in game.World.Query<Transform>())
        {
            positions.Add(row.First.Position);
        }

        return positions;
    }

    private static float AverageGapToSchoolCentre(ReefGame game)
    {
        var sums = new Dictionary<int, (Vector3 Sum, int Count)>();
        foreach (var row in game.World.Query<Transform, Fish>())
        {
            sums.TryGetValue(row.Second.School, out var school);
            sums[row.Second.School] = (school.Sum + row.First.Position, school.Count + 1);
        }

        float gaps = 0f;
        int fish = 0;
        foreach (var row in game.World.Query<Transform, Fish>())
        {
            var school = sums[row.Second.School];
            gaps += Vector3.Distance(row.First.Position, school.Sum / school.Count);
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
