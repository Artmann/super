using System.Numerics;

namespace Super.Tests;

public class PartMaskDrawTests
{
    private static readonly ModelRef Body = new(3);

    [Fact]
    public void A_masked_draw_keeps_the_mask_it_was_given()
    {
        var view = new View();
        var mask = new PartMask(3, 4, [1, 3]);
        view.Begin(0f);

        view.Model(7, Body, Matrix4x4.Identity, mask);

        Assert.Same(mask, view.Drawn[0].Parts);
        Assert.Equal(Body, view.Drawn[0].Model);
    }

    [Fact]
    public void A_plain_draw_has_no_mask()
    {
        var view = new View();
        view.Begin(0f);

        view.Model(7, Body, Matrix4x4.Identity);
        view.Model(8, Body, Matrix4x4.Identity, new Vector3(1f, 0f, 0f));

        Assert.Null(view.Drawn[0].Parts);
        Assert.Null(view.Drawn[1].Parts);
    }

    [Fact]
    public void A_mask_made_for_another_model_is_explained()
    {
        var view = new View();
        var mask = new PartMask(5, 4, [0]);
        view.Begin(0f);

        var problem = Assert.Throws<ArgumentException>(() => view.Model(7, Body, Matrix4x4.Identity, mask));

        Assert.Equal("parts", problem.ParamName);
        Assert.StartsWith("The parts mask was made for model 5, not model 3. Make it with Content.Parts for this model.", problem.Message);
        Assert.Empty(view.Drawn.ToArray());
    }

    [Fact]
    public void A_pose_with_a_mask_keeps_both()
    {
        var view = new View();
        var pose = new AnimationPose(Animation.TestRig.Rig());
        var mask = new PartMask(3, 4, [2]);
        view.Begin(0.5f, tick: 3, tickSeconds: 0.1f);

        view.Model(7, Body, Matrix4x4.Identity, pose, mask);

        Assert.Same(pose, view.Drawn[0].Pose);
        Assert.Same(mask, view.Drawn[0].Parts);
        Assert.Equal(0.05f, view.Drawn[0].PoseAhead, 0.0001f);
    }

    [Fact]
    public void An_animator_with_a_mask_keeps_its_pose_and_the_mask()
    {
        var view = new View();
        var rig = Animation.TestRig.Rig();
        rig.Model = Body;
        var animator = new AnimationController<Still, Still>(rig).State(Still.Only, rig.Clip("idle")).Start(Still.Only);
        var mask = new PartMask(3, 4, [0, 1]);
        view.Begin(0f);

        view.Model(3, animator, Matrix4x4.Identity, mask);

        Assert.Equal(Body, view.Drawn[0].Model);
        Assert.Same(animator.Pose, view.Drawn[0].Pose);
        Assert.Same(mask, view.Drawn[0].Parts);
    }

    [Fact]
    public void A_cached_draw_replays_the_mask()
    {
        var view = new View();
        var mask = new PartMask(3, 4, [1]);

        view.Begin(0f, tick: 1, tickSeconds: 0.1f);
        view.Cached(9, 1, mask, static (parts, target) => target.Model(1, Body, Matrix4x4.Identity, parts));
        view.Begin(0f, tick: 2, tickSeconds: 0.1f);
        view.Cached(9, 1, mask, static (parts, target) => target.Model(1, Body, Matrix4x4.Identity, parts));

        Assert.Same(mask, Assert.Single(view.Drawn.ToArray()).Parts);
    }

    [Fact]
    public void The_selection_keeps_exactly_the_masked_parts()
    {
        var mask = new PartMask(3, 4, [1, 3]);

        var shown = Enumerable.Range(0, 4).Select(part => SceneRenderer.ShowsPart(mask, part)).ToArray();

        Assert.Equal(2, SceneRenderer.KeptCount(4, mask));
        Assert.Equal([false, true, false, true], shown);
    }

    [Fact]
    public void A_null_mask_keeps_every_part()
    {
        var shown = Enumerable.Range(0, 4).Select(part => SceneRenderer.ShowsPart(null, part)).ToArray();

        Assert.Equal(4, SceneRenderer.KeptCount(4, null));
        Assert.Equal([true, true, true, true], shown);
    }

    [Fact]
    public void A_mask_that_keeps_nothing_keeps_no_part()
    {
        var mask = new PartMask(3, 4, ReadOnlySpan<int>.Empty);

        Assert.Equal(0, SceneRenderer.KeptCount(4, mask));
        Assert.Equal([false, false, false, false], Enumerable.Range(0, 4).Select(part => SceneRenderer.ShowsPart(mask, part)).ToArray());
    }

    [Fact]
    public void Parts_past_the_first_64_are_selected()
    {
        var mask = new PartMask(3, 70, [0, 63, 64, 69]);

        Assert.Equal(4, SceneRenderer.KeptCount(70, mask));
        Assert.True(SceneRenderer.ShowsPart(mask, 64));
        Assert.True(SceneRenderer.ShowsPart(mask, 69));
        Assert.False(SceneRenderer.ShowsPart(mask, 65));
    }

    [Fact]
    public void A_mask_for_a_model_with_another_part_count_is_explained()
    {
        var mask = new PartMask(3, 4, [0]);

        var problem = Assert.Throws<InvalidOperationException>(() => SceneRenderer.KeptCount(5, mask));

        Assert.Equal("The parts mask was made for a model with 4 parts, but the model being drawn has 5. Make it from the same cooked file.", problem.Message);
    }

    private enum Still
    {
        Only,
    }
}
