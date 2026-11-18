using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class ShadowTileCacheTests
{
    private static readonly ShadowView Tile = new(Matrix4x4.CreateTranslation(1f, 2f, 3f), Vector3.Zero, 10f, 0, 0, 256);
    private static readonly ShadowView Other = new(Matrix4x4.CreateTranslation(4f, 2f, 3f), Vector3.Zero, 10f, 256, 0, 256);
    private static readonly MaterialHandle Leaves = new(3);
    private static readonly Func<MaterialHandle, int> AllAlike = _ => 0;

    [Fact]
    public void A_tile_whose_light_and_casters_stand_still_is_drawn_once()
    {
        var cache = new ShadowTileCache();
        DrawItem[] casters = [Crate(Vector3.Zero), Crate(new Vector3(2f, 0f, 0f))];

        Assert.True(Frame(cache, Tile, casters));
        Assert.False(Frame(cache, Tile, casters));
        Assert.False(Frame(cache, Tile, casters));
    }

    [Fact]
    public void A_caster_that_moves_draws_the_tile_again()
    {
        var cache = new ShadowTileCache();
        Frame(cache, Tile, [Crate(Vector3.Zero)]);

        Assert.True(Frame(cache, Tile, [Crate(new Vector3(0.01f, 0f, 0f))]));
    }

    [Fact]
    public void A_caster_that_leaves_the_tile_draws_it_again()
    {
        var cache = new ShadowTileCache();
        Frame(cache, Tile, [Crate(Vector3.Zero), Crate(Vector3.One)]);

        Assert.True(Frame(cache, Tile, [Crate(Vector3.Zero)]));
    }

    [Fact]
    public void A_light_that_moves_draws_its_tile_again()
    {
        var cache = new ShadowTileCache();
        Frame(cache, Tile, [Crate(Vector3.Zero)]);

        Assert.True(Frame(cache, Tile with { Matrix = Matrix4x4.CreateTranslation(1f, 2.5f, 3f) }, [Crate(Vector3.Zero)]));
    }

    [Fact]
    public void A_tile_given_another_place_in_the_atlas_is_drawn_there()
    {
        var cache = new ShadowTileCache();
        Frame(cache, Tile, [Crate(Vector3.Zero)]);

        Assert.True(Frame(cache, Tile with { X = 512 }, [Crate(Vector3.Zero)]));
        Assert.True(Frame(cache, Tile with { Size = 128 }, [Crate(Vector3.Zero)]));
    }

    [Fact]
    public void A_place_another_tile_used_in_between_is_drawn_again()
    {
        var cache = new ShadowTileCache();
        Frame(cache, Tile, [Crate(Vector3.Zero)]);

        // The other tile overlaps where this one was, so what this one drew there may be gone.
        Frame(cache, Tile with { Size = 512, Matrix = Matrix4x4.Identity }, [Crate(Vector3.Zero)]);

        Assert.True(Frame(cache, Tile, [Crate(Vector3.Zero)]));
    }

    [Fact]
    public void Tiles_are_told_apart_by_their_place()
    {
        var cache = new ShadowTileCache();
        cache.Begin(0);
        cache.NeedsDrawing(Tile, [Crate(Vector3.Zero)], default, AllAlike);
        cache.NeedsDrawing(Other, [Crate(Vector3.One)], default, AllAlike);
        cache.End();

        cache.Begin(0);
        Assert.False(cache.NeedsDrawing(Tile, [Crate(Vector3.Zero)], default, AllAlike));
        Assert.True(cache.NeedsDrawing(Other, [Crate(Vector3.Zero)], default, AllAlike));
        cache.End();
    }

    [Fact]
    public void A_skinned_caster_draws_its_tile_every_frame()
    {
        var cache = new ShadowTileCache();
        var skinned = Crate(Vector3.Zero) with { Bones = new BoneRange(0, 20) };

        Assert.True(Frame(cache, Tile, [Crate(Vector3.One), skinned]));
        Assert.True(Frame(cache, Tile, [Crate(Vector3.One), skinned]));
    }

    [Fact]
    public void A_caster_swaying_in_the_wind_draws_its_tile_every_frame_and_a_calm_keeps_it()
    {
        var cache = new ShadowTileCache();
        DrawItem[] plant = [Crate(Vector3.Zero) with { Material = Leaves }];
        Func<MaterialHandle, float> sway = material => material == Leaves ? 0.5f : 0f;
        var windy = new WindReach(1f, sway);
        var calm = new WindReach(0f, sway);

        Assert.True(Frame(cache, Tile, plant, windy));
        Assert.True(Frame(cache, Tile, plant, windy));

        // Once the wind drops, the plant is drawn standing up once, then kept.
        Assert.True(Frame(cache, Tile, plant, calm));
        Assert.False(Frame(cache, Tile, plant, calm));
    }

    [Fact]
    public void A_swimming_caster_draws_its_tile_again_only_when_its_stroke_moves_on()
    {
        var cache = new ShadowTileCache();
        var fish = Crate(Vector3.Zero) with { Swim = new SwimMotion(0.2f, 1f, 0.5f) };
        Frame(cache, Tile, [fish]);

        Assert.False(Frame(cache, Tile, [fish]));
        Assert.True(Frame(cache, Tile, [fish with { Swim = new SwimMotion(0.2f, 1f, 0.6f) }]));
    }

    [Fact]
    public void A_caster_whose_material_changes_shape_draws_the_tile_again()
    {
        var cache = new ShadowTileCache();
        DrawItem[] casters = [Crate(Vector3.Zero)];
        Frame(cache, Tile, casters);

        cache.Begin(0);
        Assert.True(cache.NeedsDrawing(Tile, casters, default, _ => 7));
        cache.End();
    }

    [Fact]
    public void A_change_to_the_world_draws_every_tile_again()
    {
        var cache = new ShadowTileCache();
        Frame(cache, Tile, [Crate(Vector3.Zero)]);

        cache.Begin(1);
        Assert.True(cache.NeedsDrawing(Tile, [Crate(Vector3.Zero)], default, AllAlike));
        cache.End();
    }

    // One frame with one tile, and whether that tile is drawn.
    private static bool Frame(ShadowTileCache cache, ShadowView tile, DrawItem[] casters, WindReach wind = default)
    {
        cache.Begin(0);
        bool drawn = cache.NeedsDrawing(tile, casters, wind, AllAlike);
        cache.End();
        return drawn;
    }

    private static DrawItem Crate(Vector3 at) => new()
    {
        Mesh = new MeshHandle(1),
        IndexCount = 36,
        Material = new MaterialHandle(2),
        Transform = Matrix4x4.CreateTranslation(at),
    };
}
