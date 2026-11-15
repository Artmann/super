using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Which draws decals lay on, and which are drawn after them instead.</summary>
public class DecalOptOutTests
{
    private static DrawItem Draw(int bones = 0, bool seeThroughGhost = false) => new()
    {
        Bones = new BoneRange(0, bones),
        Ghost = seeThroughGhost ? new Ghost(new Vector3(1f), 0.5f) : null,
    };

    [Fact]
    public void A_plain_solid_draw_takes_decals_and_is_drawn_before_them()
    {
        Assert.True(SceneDraws.Belongs(Draw(), MaterialFlags.None, ScenePass.OpaqueTakingDecals));
        Assert.False(SceneDraws.Belongs(Draw(), MaterialFlags.None, ScenePass.OpaqueAfterDecals));
    }

    [Fact]
    public void A_skinned_draw_never_takes_decals()
    {
        Assert.False(SceneDraws.Belongs(Draw(bones: 30), MaterialFlags.None, ScenePass.OpaqueTakingDecals));
        Assert.True(SceneDraws.Belongs(Draw(bones: 30), MaterialFlags.None, ScenePass.OpaqueAfterDecals));
    }

    [Fact]
    public void A_material_with_no_decals_is_drawn_after_them()
    {
        Assert.False(SceneDraws.Belongs(Draw(), MaterialFlags.NoDecals, ScenePass.OpaqueTakingDecals));
        Assert.True(SceneDraws.Belongs(Draw(), MaterialFlags.NoDecals, ScenePass.OpaqueAfterDecals));
    }

    [Fact]
    public void Glass_is_in_neither_opaque_round_and_every_solid_draw_is_in_exactly_one()
    {
        Assert.False(SceneDraws.Belongs(Draw(), MaterialFlags.Transparent, ScenePass.OpaqueTakingDecals));
        Assert.False(SceneDraws.Belongs(Draw(), MaterialFlags.Transparent | MaterialFlags.NoDecals, ScenePass.OpaqueAfterDecals));
        Assert.False(SceneDraws.Belongs(Draw(seeThroughGhost: true), MaterialFlags.None, ScenePass.OpaqueTakingDecals));
        foreach (var (draw, flags) in new[] { (Draw(), MaterialFlags.None), (Draw(bones: 4), MaterialFlags.None), (Draw(), MaterialFlags.NoDecals) })
        {
            bool before = SceneDraws.Belongs(draw, flags, ScenePass.OpaqueTakingDecals);
            bool after = SceneDraws.Belongs(draw, flags, ScenePass.OpaqueAfterDecals);
            Assert.True(before != after, $"A draw with {draw.Bones.Count} bones and {flags} is in {(before ? "both" : "neither")} round.");
        }
    }

    [Fact]
    public void A_surface_with_no_decals_sets_the_flag()
    {
        Assert.Equal(MaterialFlags.NoDecals, SceneRenderer.FlagsOf(new Surface { NoDecals = true }) & MaterialFlags.NoDecals);
        Assert.Equal(MaterialFlags.None, SceneRenderer.FlagsOf(new Surface()) & MaterialFlags.NoDecals);
    }
}
