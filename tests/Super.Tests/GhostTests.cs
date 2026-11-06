using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Ghosts: a model drawn in one colour, as a preview of where something will go.</summary>
public class GhostTests
{
    private static readonly Vector3 Green = new(0.2f, 1f, 0.3f);

    private static DrawItem Item(Ghost? ghost = null) => new() { Transform = Matrix4x4.Identity, Ghost = ghost };

    [Fact]
    public void View_ghost_draws_the_model_with_its_colour_and_opacity()
    {
        var view = new View();
        view.Begin(0f);

        view.Ghost(7, ModelRef.Cube, Matrix4x4.Identity, Green, opacity: 0.5f);

        var draw = Assert.Single(view.Drawn.ToArray());
        Assert.Equal(7, draw.Key);
        Assert.Equal(new Ghost(Green, 0.5f), draw.Ghost);
    }

    [Fact]
    public void View_ghost_is_solid_unless_asked()
    {
        var view = new View();
        view.Begin(0f);

        view.Ghost(7, ModelRef.Cube, Matrix4x4.Identity, Green);

        Assert.Equal(1f, view.Drawn[0].Ghost!.Value.Opacity);
    }

    [Fact]
    public void A_plain_model_is_no_ghost()
    {
        var view = new View();
        view.Begin(0f);

        view.Model(7, ModelRef.Cube, Matrix4x4.Identity, Green);

        Assert.Null(view.Drawn[0].Ghost);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.5f)]
    [InlineData(1.5f)]
    [InlineData(float.NaN)]
    public void View_ghost_with_an_opacity_outside_zero_to_one_is_explained(float opacity)
    {
        var view = new View();
        view.Begin(0f);

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => view.Ghost(7, ModelRef.Cube, Matrix4x4.Identity, Green, opacity));

        Assert.StartsWith("A ghost's opacity must be more than 0 and at most 1.", problem.Message);
    }

    [Fact]
    public void A_solid_ghost_is_drawn_with_solid_things_and_casts_no_shadow()
    {
        var ghost = Item(new Ghost(Green, 1f));

        Assert.True(SceneDraws.Belongs(ghost, MaterialFlags.None, ScenePass.Opaque));
        Assert.False(SceneDraws.Belongs(ghost, MaterialFlags.None, ScenePass.Transparent));
        Assert.False(SceneDraws.Belongs(ghost, MaterialFlags.None, ScenePass.Shadow));
    }

    [Fact]
    public void A_see_through_ghost_is_drawn_with_glass_and_casts_no_shadow()
    {
        var ghost = Item(new Ghost(Green, 0.4f));

        Assert.False(SceneDraws.Belongs(ghost, MaterialFlags.None, ScenePass.Opaque));
        Assert.True(SceneDraws.Belongs(ghost, MaterialFlags.None, ScenePass.Transparent));
        Assert.False(SceneDraws.Belongs(ghost, MaterialFlags.None, ScenePass.Shadow));
    }

    [Fact]
    public void A_solid_ghost_of_glass_is_solid()
    {
        var ghost = Item(new Ghost(Green, 1f));

        Assert.True(SceneDraws.Belongs(ghost, MaterialFlags.Transparent, ScenePass.Opaque));
        Assert.False(SceneDraws.Belongs(ghost, MaterialFlags.Transparent, ScenePass.Transparent));
    }

    [Fact]
    public void Plain_draws_go_where_their_material_says()
    {
        var plain = Item();

        Assert.True(SceneDraws.Belongs(plain, MaterialFlags.None, ScenePass.Opaque));
        Assert.True(SceneDraws.Belongs(plain, MaterialFlags.None, ScenePass.Shadow));
        Assert.False(SceneDraws.Belongs(plain, MaterialFlags.None, ScenePass.Transparent));
        Assert.True(SceneDraws.Belongs(plain, MaterialFlags.Transparent, ScenePass.Transparent));
        Assert.False(SceneDraws.Belongs(plain, MaterialFlags.Transparent, ScenePass.Opaque));
        Assert.True(SceneDraws.Belongs(plain, MaterialFlags.Transparent, ScenePass.Shadow));
    }

    [Fact]
    public void A_ghost_reaches_the_shaders_as_its_colour_and_opacity()
    {
        var constants = SceneDraws.Constants(Item(new Ghost(Green, 0.4f)), cascade: 0);

        Assert.Equal(new Vector4(0.2f, 1f, 0.3f, 0.4f), constants.Ghost);
    }

    [Fact]
    public void A_plain_draw_reaches_the_shaders_with_no_ghost()
    {
        var constants = SceneDraws.Constants(Item(), cascade: 0);

        Assert.Equal(0f, constants.Ghost.W);
    }
}
