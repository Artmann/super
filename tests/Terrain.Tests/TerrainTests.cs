using System.Numerics;
using Rendition;
using Super;
using Super.Terrain;
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
        Assert.Equal(after.Count + Road.Models, run.Drawn.Select(draw => draw.Model).Distinct().Count());
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

    [Fact]
    public void The_road_stays_above_the_ground_all_the_way_round()
    {
        using var run = HeadlessGame.Create<TerrainGame>(seed: 7);
        run.Ticks(1);

        var road = run.Game.Road.Path;
        foreach (var pose in road.Scatter(1f))
        {
            foreach (float side in (ReadOnlySpan<float>)[-3f, 0f, 3f])
            {
                var spot = pose.Position + pose.Right * side;
                float ground = run.Game.GroundHeight(spot.X, spot.Z);
                Assert.True(spot.Y > ground, $"The road dips {ground - spot.Y} m under the ground at {spot}.");
            }
        }
    }

    [Fact]
    public void The_cart_drives_round_the_road()
    {
        using var run = HeadlessGame.Create<TerrainGame>(seed: 7);

        // Two seconds at 30 ticks a second.
        run.Ticks(60);

        Assert.Equal(2f * Road.CartSpeed, run.Game.Road.CartDistance, 0.01f);
        Assert.True(run.Game.Road.Path.Nearest(run.Game.Road.Cart).Gap < 0.01f);
    }

    [Fact]
    public void F_plays_a_flyover_that_watches_the_cart()
    {
        using var run = HeadlessGame.Create<TerrainGame>(seed: 7);
        run.Ticks(1);
        var flying = run.Camera;

        run.Input.Press(Key.F);
        run.Ticks(60);
        run.Frame(1f);

        Assert.NotEqual(flying.Position, run.Camera.Position);
        float gap = Vector3.Distance(run.Game.Road.Cart + new Vector3(0f, 1f, 0f), run.Camera.Target);
        Assert.True(gap < 0.5f, $"The camera looks {gap} m away from the cart.");
    }

    [Fact]
    public void A_ground_chunk_spans_its_lightmap_from_corner_to_corner()
    {
        using var run = HeadlessGame.Create<TerrainGame>(seed: 7);
        run.Ticks(1);
        var field = new Heightfield(run.Game.GroundHeight, TerrainGame.Spacing);

        var chunk = field.Chunk(1, -2, TerrainGame.ChunkQuads, new Surface(), 0.25f);

        int side = TerrainGame.ChunkQuads + 1;
        Assert.Equal(Vector2.Zero, chunk.Vertices[0].Uv2);
        Assert.Equal(new Vector2(1f, 0f), chunk.Vertices[side - 1].Uv2);
        Assert.Equal(Vector2.One, chunk.Vertices[side * side - 1].Uv2);
        Assert.Null(Problem(chunk));
    }

    [Fact]
    public void The_road_has_a_valid_lightmap_set()
    {
        using var run = HeadlessGame.Create<TerrainGame>(seed: 7);
        run.Ticks(1);
        Vector2[] embankment = [new(3.8f, -1.6f), new(3f, 0f), new(-3f, 0f), new(-3.8f, -1.6f)];

        var road = Shapes.Extrude(run.Game.Road.Path, embankment, step: 1f);

        Assert.Null(Problem(road));
    }

    private static string? Problem(GeneratedMesh mesh) =>
        LightmapUnwrap.Problem(mesh.Vertices.Select(v => v.Position).ToArray(), mesh.Vertices.Select(v => v.Uv2).ToArray(), mesh.Indices);
}
