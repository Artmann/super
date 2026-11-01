using Super;
using Super.Testing;
using Terrain;

namespace Terrain.Tests;

public class TerrainTests
{
    [Fact]
    public void Chunks_load_around_the_camera_and_unload_behind()
    {
        using var run = HeadlessGame.Create<TerrainGame>(seed: 7);
        run.Ticks(1);

        var start = run.Game.LoadedChunks.ToHashSet();
        Assert.Equal((2 * TerrainGame.LoadRadius + 1) * (2 * TerrainGame.LoadRadius + 1), start.Count);
        Assert.Contains((0, TerrainGame.LoadRadius), start);
        Assert.DoesNotContain((0, -TerrainGame.LoadRadius - 2), start);

        // Fly forward, which is towards -z, for two chunks' worth of ground.
        run.Input.Hold(Key.W);
        run.Ticks(200);

        float travelled = -run.Game.Position.Z;
        Assert.InRange(travelled, 2f * TerrainGame.ChunkSize, 3f * TerrainGame.ChunkSize);
        var after = run.Game.LoadedChunks.ToHashSet();
        Assert.Contains((0, -TerrainGame.LoadRadius - 2), after);
        Assert.DoesNotContain((0, TerrainGame.LoadRadius), after);
        Assert.NotEqual(start, after);
        Assert.Equal(after.Count, run.Drawn.Select(draw => draw.Model).Distinct().Count());
    }

    [Fact]
    public void The_camera_stays_above_the_ground()
    {
        using var run = HeadlessGame.Create<TerrainGame>(seed: 3);
        run.Invariant("the camera is above the ground", game =>
            game.Position.Y > game.GroundHeight(game.Position.X, game.Position.Z));

        run.Input.Hold(Key.W);
        run.Input.Hold(Key.LeftShift);
        run.Ticks(600);
    }

    [Fact]
    public void Steady_run_allocates_nothing_per_tick()
    {
        using var run = HeadlessGame.Create<TerrainGame>(seed: 1);
        run.Ticks(300);

        long before = GC.GetAllocatedBytesForCurrentThread();
        run.Ticks(300);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
