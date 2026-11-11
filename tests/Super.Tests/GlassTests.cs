using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Glass: sorted with particles, seeing the scene through a copy, and made from a surface.</summary>
public class GlassTests
{
    // Looking down -z from the origin, so a point's depth is minus its z.
    private static readonly Vector3 Eye = Vector3.Zero;
    private static readonly Vector3 Forward = -Vector3.UnitZ;
    private static readonly Matrix4x4 ViewProjection =
        Matrix4x4.CreateLookAt(Eye, Forward, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 100f);

    // A corner of the screen that the middle, where the particles are, doesn't reach.
    private static readonly ScreenRect Corner = new(0.8f, 0.8f, 0.9f, 0.9f);

    private static GlassItem Glass(int draw, float depth, ScreenRect? rect = null, bool canRefract = true) =>
        new(draw, depth, rect ?? ScreenRect.Full, canRefract);

    private static Particle ParticleAt(float depth) => new() { Position = new Vector3(0f, 0f, -depth), Size = 0.1f };

    private static List<TransparentStep> Plan(GlassItem[] glass, params Particle[] particles)
    {
        var steps = new List<TransparentStep>();
        TransparentPlan.Build(glass, particles, Eye, Forward, ViewProjection, steps);
        return steps;
    }

    private static TransparentStep Copy => new(TransparentStepKind.Copy, 0);

    private static TransparentStep Drawn(int draw, bool refracts) => new(TransparentStepKind.Glass, draw, Refracts: refracts);

    private static TransparentStep Particles(int first, int count) => new(TransparentStepKind.Particles, first, count);

    [Fact]
    public void Glass_is_drawn_furthest_first_and_panes_apart_share_one_copy()
    {
        var steps = Plan([Glass(0, 2f, Corner), Glass(1, 8f, new ScreenRect(-0.9f, -0.9f, -0.8f, -0.8f)), Glass(2, 5f, new ScreenRect(0.8f, -0.9f, 0.9f, -0.8f))]);

        Assert.Equal([Copy, Drawn(1, true), Drawn(2, true), Drawn(0, true)], steps);
    }

    [Fact]
    public void Glass_at_the_same_depth_keeps_the_order_it_was_given()
    {
        var steps = Plan([Glass(4, 3f, Corner), Glass(2, 3f, new ScreenRect(-0.9f, -0.9f, -0.8f, -0.8f))]);

        Assert.Equal([Copy, Drawn(2, true), Drawn(4, true)], steps);
    }

    [Fact]
    public void Particles_are_drawn_between_the_glass_in_front_and_behind_them()
    {
        // Particles at depths 10, 6, 3 and 1, already furthest first. Panes at 7 and 2.
        var steps = Plan([Glass(0, 7f, Corner), Glass(1, 2f, new ScreenRect(-0.9f, -0.9f, -0.8f, -0.8f))], ParticleAt(10f), ParticleAt(6f), ParticleAt(3f), ParticleAt(1f));

        Assert.Equal(
            [Particles(0, 1), Copy, Drawn(0, true), Particles(1, 2), Drawn(1, true), Particles(3, 1)],
            steps);
    }

    [Fact]
    public void Glass_behind_every_particle_is_copied_before_any_particle_is_drawn()
    {
        var steps = Plan([Glass(0, 20f, Corner)], ParticleAt(10f), ParticleAt(5f));

        Assert.Equal([Copy, Drawn(0, true), Particles(0, 2)], steps);
    }

    [Fact]
    public void Only_particles_are_one_run()
    {
        var steps = Plan([], ParticleAt(4f), ParticleAt(2f));

        Assert.Equal([Particles(0, 2)], steps);
    }

    [Fact]
    public void Glass_over_glass_is_copied_again_so_it_sees_the_pane_behind()
    {
        var steps = Plan([Glass(0, 5f), Glass(1, 3f)]);

        Assert.Equal([Copy, Drawn(0, true), Copy, Drawn(1, true)], steps);
    }

    [Fact]
    public void Glass_over_particles_drawn_since_the_copy_is_copied_again()
    {
        // The particle is in the middle of the screen, which the second pane covers and the first doesn't.
        var steps = Plan([Glass(0, 8f, Corner), Glass(1, 2f, new ScreenRect(-0.1f, -0.1f, 0.1f, 0.1f))], ParticleAt(5f));

        Assert.Equal([Copy, Drawn(0, true), Particles(0, 1), Copy, Drawn(1, true)], steps);
    }

    [Fact]
    public void After_the_most_copies_overlapping_glass_blends_without_bending()
    {
        var glass = Enumerable.Range(0, TransparentPlan.MaxCopies + 2).Select(i => Glass(i, 10f - i)).ToArray();

        var steps = Plan(glass);

        Assert.Equal(TransparentPlan.MaxCopies, steps.Count(step => step.Kind == TransparentStepKind.Copy));
        Assert.Equal(
            [true, true, true, true, false, false],
            steps.Where(step => step.Kind == TransparentStepKind.Glass).Select(step => step.Refracts));
    }

    [Fact]
    public void See_through_ghosts_never_copy_or_bend()
    {
        var steps = Plan([Glass(0, 5f, canRefract: false), Glass(1, 3f, canRefract: false)]);

        Assert.Equal([Drawn(0, false), Drawn(1, false)], steps);
    }

    [Fact]
    public void Glass_after_a_ghost_over_it_is_copied_so_it_sees_the_ghost()
    {
        var steps = Plan([Glass(0, 5f, canRefract: false), Glass(1, 3f)]);

        Assert.Equal([Drawn(0, false), Copy, Drawn(1, true)], steps);
    }

    [Fact]
    public void A_box_in_front_lands_round_the_middle_of_the_screen()
    {
        var rect = ScreenRect.OfBox(new Vector3(-1f, -1f, -11f), new Vector3(1f, 1f, -9f), ViewProjection);

        // The near face, 9 m away, is widest: 1 / (9 * tan(0.5)) across each way.
        float reach = 1f / (9f * MathF.Tan(0.5f));
        Assert.Equal(-reach, rect.MinX, 4);
        Assert.Equal(reach, rect.MaxX, 4);
        Assert.Equal(-reach, rect.MinY, 4);
        Assert.Equal(reach, rect.MaxY, 4);
    }

    [Fact]
    public void A_box_reaching_behind_the_camera_covers_the_whole_screen()
    {
        var rect = ScreenRect.OfBox(new Vector3(-1f, -1f, -5f), new Vector3(1f, 1f, 1f), ViewProjection);

        Assert.Equal(ScreenRect.Full, rect);
    }

    [Fact]
    public void Rects_overlap_when_they_touch_and_not_when_apart()
    {
        var left = new ScreenRect(-1f, -1f, 0f, 1f);

        Assert.True(left.Overlaps(new ScreenRect(0f, 0f, 1f, 1f)));
        Assert.False(left.Overlaps(new ScreenRect(0.1f, -1f, 1f, 1f)));
        Assert.False(new ScreenRect(0.1f, -1f, 1f, 1f).Overlaps(left));
        Assert.False(left.Overlaps(new ScreenRect(-1f, 1.1f, 0f, 2f)));
        Assert.False(ScreenRect.Empty.Overlaps(ScreenRect.Full));
        Assert.False(ScreenRect.Full.Overlaps(ScreenRect.Empty));
    }

    [Fact]
    public void The_view_gives_the_eye_and_where_it_looks()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(1f, 2f, 3f), new Vector3(4f, 2f, 3f), Vector3.UnitY);

        var glassView = TransparentView.From(view, Matrix4x4.Identity);

        Assert.Equal(1f, glassView.Eye.X, 5);
        Assert.Equal(2f, glassView.Eye.Y, 5);
        Assert.Equal(3f, glassView.Eye.Z, 5);
        Assert.Equal(1f, glassView.Forward.X, 5);
        Assert.Equal(0f, glassView.Forward.Y, 5);
        Assert.Equal(0f, glassView.Forward.Z, 5);
    }

    [Fact]
    public void A_draw_without_bounds_is_its_origin_and_reaches_everywhere()
    {
        var draw = new DrawItem { Transform = Matrix4x4.CreateTranslation(1f, 2f, 3f) };

        var (center, radius) = TransparentPass.Bounds(draw, [default]);

        Assert.Equal(new Vector3(1f, 2f, 3f), center);
        Assert.Equal(float.PositiveInfinity, radius);
    }

    [Fact]
    public void A_draws_bounds_move_with_it_and_grow_with_its_largest_scale()
    {
        var draw = new DrawItem
        {
            Transform = Matrix4x4.CreateScale(1f, 3f, 2f) * Matrix4x4.CreateTranslation(10f, 0f, 0f),
            Detail = new DetailHandle(1),
        };
        List<PartDetail> details = [default, new PartDetail(new Vector3(0f, 1f, 0f), 0.5f, [])];

        var (center, radius) = TransparentPass.Bounds(draw, details);

        // The centre is lifted 1 and stretched 3 high, then moved 10 along x.
        Assert.Equal(new Vector3(10f, 3f, 0f), center);
        Assert.Equal(1.5f, radius);
    }

    [Fact]
    public void A_swaying_draws_bounds_grow_by_its_sway_after_its_scale()
    {
        var draw = new DrawItem
        {
            Transform = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(10f, 0f, 0f),
            Detail = new DetailHandle(1),
            Material = new MaterialHandle(3),
        };
        List<PartDetail> details = [default, new PartDetail(Vector3.Zero, 0.5f, [])];

        var (_, radius) = TransparentPass.Bounds(draw, details, new WindReach(0.4f, material => material.Index == 3 ? 0.5f : 0f));

        // 0.5 scaled twice is 1, then 0.4 × 0.5 of lean and 0.02 of flutter.
        Assert.Equal(1.22f, radius, 1e-6f);
    }

    [Fact]
    public void A_surface_below_full_opacity_is_glass_with_its_ior()
    {
        var material = SceneRenderer.MaterialOf(new Surface { Color = new Vector3(0.2f, 0.4f, 0.6f), Opacity = 0.3f, Ior = 1.5f });

        Assert.Equal(MaterialFlags.Transparent, material.Flags);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 0.3f), material.BaseColor);
        Assert.Equal(1.5f, material.Ior);
    }

    [Fact]
    public void A_surface_at_full_opacity_is_solid()
    {
        var material = SceneRenderer.MaterialOf(new Surface());

        Assert.Equal(MaterialFlags.None, material.Flags);
        Assert.Equal(1f, material.BaseColor.W);
    }

    [Fact]
    public void Plain_glass_casts_a_shadow_but_see_through_ghosts_do_not()
    {
        var plain = new DrawItem { Transform = Matrix4x4.Identity };
        var ghost = plain with { Ghost = new Ghost(Vector3.One, 0.5f) };

        Assert.True(SceneDraws.Belongs(plain, MaterialFlags.Transparent, ScenePass.Shadow));
        Assert.False(SceneDraws.Belongs(ghost, MaterialFlags.Transparent, ScenePass.Shadow));
    }
}
