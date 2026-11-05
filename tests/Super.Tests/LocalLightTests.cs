using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class LocalLightTests
{
    private const int Height = 720;

    // A camera at the origin looking down -z, as in most of these tests.
    private static readonly Matrix4x4 LookingDownZ = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
    private static readonly Matrix4x4 Perspective = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 3f, 16f / 9f, 0.1f);

    [Fact]
    public void The_gpu_lights_and_tiles_match_light_data_glsl()
    {
        Assert.Equal(64, Marshal.SizeOf<GpuLocalLight>());
        Assert.Equal(96, Marshal.SizeOf<GpuShadowTile>());
        Assert.Equal(64, (int)Marshal.OffsetOf<GpuShadowTile>(nameof(GpuShadowTile.Rect)));
        Assert.Equal(80, (int)Marshal.OffsetOf<GpuShadowTile>(nameof(GpuShadowTile.Depth)));
    }

    [Fact]
    public void Lights_whose_reach_misses_the_view_are_left_out()
    {
        LocalLight[] lights =
        [
            Point(1, new Vector3(0f, 0f, -5f), 2f),
            Point(2, new Vector3(0f, 0f, 5f), 2f),     // Behind the camera, out of reach.
            Point(3, new Vector3(0f, 0f, 1f), 2f),     // Behind, but its reach comes round in front.
            Point(4, new Vector3(40f, 0f, -5f), 2f),   // Far off to the side.
        ];

        var (prepared, gpu, _) = Prepare(lights);

        Assert.Equal(2, prepared.LightCount);
        Assert.Equal(new Vector4(0f, 0f, -5f, 2f), gpu[0].PositionRange);
        Assert.Equal(new Vector4(0f, 0f, 1f, 2f), gpu[1].PositionRange);

        // The clusters span from as near as the view starts to as far as the furthest reach.
        Assert.Equal(0.1f, prepared.NearDepth, 4);
        Assert.Equal(7f, prepared.FarDepth, 4);
        Assert.False(prepared.Parallel);
    }

    [Fact]
    public void A_point_light_marks_itself_and_a_spot_carries_its_cone()
    {
        LocalLight[] lights =
        [
            Point(1, new Vector3(0f, 0f, -5f), 2f) with { CastsShadow = false },
            new LocalLight
            {
                Key = 2, Position = new Vector3(0f, 2f, -5f), Radiance = Vector3.One, Range = 4f,
                Direction = -Vector3.UnitY * 3f, InnerAngle = 0.2f, OuterAngle = 0.5f,
            },
        ];

        var (_, gpu, _) = Prepare(lights);

        Assert.Equal(-2f, gpu[0].DirectionCone.W);
        Assert.Equal(-1f, gpu[0].RadianceShadow.W);
        Assert.Equal(new Vector3(0f, -1f, 0f), new Vector3(gpu[1].DirectionCone.X, gpu[1].DirectionCone.Y, gpu[1].DirectionCone.Z));
        Assert.Equal(MathF.Cos(0.5f), gpu[1].DirectionCone.W, 5);
        Assert.Equal(1f / (MathF.Cos(0.2f) - MathF.Cos(0.5f)), gpu[1].Fade.X, 3);
    }

    [Fact]
    public void A_point_lights_shadow_takes_six_tiles_and_a_spots_one()
    {
        LocalLight[] lights =
        [
            Point(1, new Vector3(0f, 0f, -5f), 2f),
            new LocalLight
            {
                Key = 2, Position = new Vector3(0f, 2f, -5f), Radiance = Vector3.One, Range = 4f,
                Direction = -Vector3.UnitY, OuterAngle = 0.5f, CastsShadow = true,
            },
        ];

        var (prepared, gpu, _) = Prepare(lights);

        Assert.Equal(7, prepared.TileCount);
        int point = (int)gpu[0].RadianceShadow.W;
        int spot = (int)gpu[1].RadianceShadow.W;
        Assert.Equal(Enumerable.Range(0, 7), Enumerable.Range(point, 6).Append(spot).Order());
    }

    [Fact]
    public void Tiles_fill_the_atlas_without_overlapping_and_the_biggest_on_screen_get_the_sharpest()
    {
        // Rows of lamps going away from the camera, more than the atlas holds even at its smallest
        // tiles: 4096 of them, and 1000 point lights would need 6000.
        var lights = Enumerable.Range(0, 1000)
            .Select(i => Point(i, Spread(i, 2f + i * 0.15f), 1.5f))
            .ToArray();

        var (prepared, gpu, _) = Prepare(lights);

        var tiles = prepared.Tiles.ToArray();
        Assert.NotEmpty(tiles);
        foreach (var tile in tiles)
        {
            Assert.True(BitOperations.IsPow2(tile.Size), $"{tile.Size} isn't a power of two");
            Assert.InRange(tile.Size, LocalLights.MinTile, LocalLights.MaxTile);
            Assert.InRange(tile.X, 0, LocalLights.AtlasSize - tile.Size);
            Assert.InRange(tile.Y, 0, LocalLights.AtlasSize - tile.Size);
        }

        for (int a = 0; a < tiles.Length; a++)
        {
            for (int b = a + 1; b < tiles.Length; b++)
            {
                bool apart = tiles[a].X + tiles[a].Size <= tiles[b].X || tiles[b].X + tiles[b].Size <= tiles[a].X
                    || tiles[a].Y + tiles[a].Size <= tiles[b].Y || tiles[b].Y + tiles[b].Size <= tiles[a].Y;
                Assert.True(apart, $"tiles {a} and {b} overlap");
            }
        }

        // The atlas ran out, so the furthest lamps have no shadow, and nearer ones never have smaller tiles.
        Assert.Equal(1000, prepared.LightCount);
        Assert.Equal(-1f, gpu[prepared.LightCount - 1].RadianceShadow.W);
        int SizeOf(int light) => gpu[light].RadianceShadow.W < 0f ? 0 : tiles[(int)gpu[light].RadianceShadow.W].Size;
        var byDistance = Enumerable.Range(0, prepared.LightCount).OrderBy(light => lights[light].Position.Length()).ToArray();
        for (int i = 1; i < byDistance.Length; i++)
        {
            Assert.True(
                SizeOf(byDistance[i - 1]) >= SizeOf(byDistance[i]),
                $"lamp {byDistance[i]} has a bigger tile than the nearer lamp {byDistance[i - 1]}");
        }
    }

    [Fact]
    public void A_tile_sees_its_face_of_the_cube_with_near_at_one_and_the_range_at_zero()
    {
        var eye = new Vector3(1f, 2f, 3f);
        var matrix = LocalLights.TileMatrix(eye, Vector3.UnitX, Vector3.UnitY, 1f, 5f);

        static Vector3 Ndc(Vector4 clip) => new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        var ahead = Ndc(Vector4.Transform(new Vector4(eye + new Vector3(2f, 0f, 0f), 1f), matrix));
        Assert.Equal(0f, ahead.X, 5);
        Assert.Equal(0f, ahead.Y, 5);
        Assert.Equal(1f, Ndc(Vector4.Transform(new Vector4(eye + Vector3.UnitX * LocalLights.ShadowNear, 1f), matrix)).Z, 4);
        Assert.Equal(0f, Ndc(Vector4.Transform(new Vector4(eye + Vector3.UnitX * 5f, 1f), matrix)).Z, 4);

        // Up in the world is up the tile, which like the camera's has y flipped.
        Assert.True(Ndc(Vector4.Transform(new Vector4(eye + new Vector3(2f, 1f, 0f), 1f), matrix)).Y < 0f);
    }

    [Fact]
    public void A_light_near_the_line_between_two_sizes_keeps_last_frames()
    {
        var prepared = new LocalLights();
        var gpu = new GpuLocalLight[LocalLight.MaxPerFrame];
        var tiles = new GpuShadowTile[LocalLights.MaxTiles];

        int SizeAt(float distance)
        {
            LocalLight[] lights = [Point(7, new Vector3(0f, 0f, -distance), 1f)];
            prepared.Prepare(lights, LookingDownZ, Perspective, Height, gpu, tiles);
            return prepared.Tiles[0].Size;
        }

        int far = SizeAt(40f);
        int near = SizeAt(4f);
        Assert.True(near > far);

        // Halfway back, it's still as sharp as it was, though a light arriving there wouldn't be.
        var fresh = new LocalLights();
        LocalLight[] halfway = [Point(8, new Vector3(0f, 0f, -6f), 1f)];
        fresh.Prepare(halfway, LookingDownZ, Perspective, Height, gpu, tiles);
        Assert.True(fresh.Tiles[0].Size < near, "the test needs a distance where a new light gets a smaller tile");
        Assert.Equal(near, SizeAt(6f));
    }

    [Fact]
    public void More_lights_than_a_frame_holds_is_explained()
    {
        var lights = new LocalLight[LocalLight.MaxPerFrame + 1];

        var error = Assert.Throws<ArgumentException>(() => Prepare(lights));

        Assert.StartsWith($"A frame can have at most {LocalLight.MaxPerFrame} point and spot lights, not {LocalLight.MaxPerFrame + 1}.", error.Message);
    }

    [Fact]
    public void A_view_keeps_its_lights_until_the_next_frame()
    {
        var view = new View();
        view.Begin(0f);

        view.PointLight(1, new Vector3(1f, 2f, 3f), new Vector3(2f, 1f, 0.5f), 4f);
        view.SpotLight(2, Vector3.Zero, new Vector3(0f, -2f, 0f), Vector3.One, 6f, 0.3f, 0.6f, shadows: false);

        Assert.Equal(2, view.DrawnLights.Length);
        Assert.Equal(new Vector3(1f, 2f, 3f), view.DrawnLights[0].Position);
        Assert.True(view.DrawnLights[0].CastsShadow);
        Assert.False(view.DrawnLights[0].IsSpot);
        Assert.Equal(-Vector3.UnitY, view.DrawnLights[1].Direction);
        Assert.False(view.DrawnLights[1].CastsShadow);

        view.Begin(0f);
        Assert.Equal(0, view.DrawnLights.Length);
    }

    [Fact]
    public void A_light_that_cant_be_is_explained()
    {
        var view = new View();
        view.Begin(0f);

        Assert.Contains(
            "A light's range must be more than 0 metres.",
            Assert.Throws<ArgumentOutOfRangeException>(() => view.PointLight(1, Vector3.Zero, Vector3.One, 0f)).Message);
        Assert.Contains(
            "A light's color can't be negative",
            Assert.Throws<ArgumentException>(() => view.PointLight(1, Vector3.Zero, -Vector3.One, 1f)).Message);
        Assert.Contains(
            "A spot light's outer angle must be more than 0 and less than π/2 radians.",
            Assert.Throws<ArgumentOutOfRangeException>(() => view.SpotLight(1, Vector3.Zero, Vector3.UnitY, Vector3.One, 1f, 0f, MathF.PI / 2f)).Message);
        Assert.Contains(
            "A spot light's inner angle must be from 0 up to its outer angle.",
            Assert.Throws<ArgumentOutOfRangeException>(() => view.SpotLight(1, Vector3.Zero, Vector3.UnitY, Vector3.One, 1f, 0.5f, 0.4f)).Message);
        Assert.Contains(
            "A spot light needs a direction to shine in",
            Assert.Throws<ArgumentException>(() => view.SpotLight(1, Vector3.Zero, Vector3.Zero, Vector3.One, 1f, 0.1f, 0.4f)).Message);

        for (int i = 0; i < LocalLight.MaxPerFrame; i++)
        {
            view.PointLight(i, Vector3.Zero, Vector3.One, 1f);
        }

        Assert.Equal(
            $"A frame can have at most {LocalLight.MaxPerFrame} point and spot lights. Leave out those too far away to see.",
            Assert.Throws<InvalidOperationException>(() => view.PointLight(0, Vector3.Zero, Vector3.One, 1f)).Message);
    }

    // The tenth of a row of ten that the lamp is, spread across most of the view at that depth.
    private static Vector3 Spread(int lamp, float depth) => new((lamp % 10 - 4.5f) / 4.5f * 0.6f * depth, 0f, -depth);

    private static LocalLight Point(int key, Vector3 position, float range) => new()
    {
        Key = key,
        Position = position,
        Radiance = Vector3.One,
        Range = range,
        CastsShadow = true,
    };

    private static (LocalLights Prepared, GpuLocalLight[] Lights, GpuShadowTile[] Tiles) Prepare(LocalLight[] lights)
    {
        var prepared = new LocalLights();
        var gpu = new GpuLocalLight[LocalLight.MaxPerFrame];
        var tiles = new GpuShadowTile[LocalLights.MaxTiles];
        prepared.Prepare(lights, LookingDownZ, Perspective, Height, gpu, tiles);
        return (prepared, gpu, tiles);
    }
}
