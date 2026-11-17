using System.Numerics;
using Rendition;

namespace Super.Cook.Tests;

public sealed class ModelInPlaceTests : TempFolders
{
    private static ModelData Cook(string folder, string sidecar) => WalkingRig.CookModel(folder, sidecar);

    /// <summary>Where the clip's frame puts the hips in the cooked model, after the skeleton's normalization.</summary>
    private static Vector3 HipsAt(ModelData model, AnimationClip clip, int frame)
    {
        var skeleton = model.Skeleton!;
        var global = new Matrix4x4[skeleton.Names.Length];
        for (int joint = 0; joint < global.Length; joint++)
        {
            var local = clip.Frames[frame * clip.JointCount + joint].Matrix;
            global[joint] = skeleton.Parents[joint] >= 0 ? local * global[skeleton.Parents[joint]] : local;
        }

        int hips = skeleton.IndexOf("hips");
        Assert.True(hips >= 0, "The skeleton has no hips joint.");
        return Vector3.Transform(Vector3.Zero, global[hips] * skeleton.RootTransform);
    }

    [Fact]
    public void An_in_place_clip_keeps_the_hips_over_where_they_start_but_lets_them_rise()
    {
        var model = Cook(Temp(), """{ "animations": [{ "file": "walk.gltf", "name": "Walk", "inPlace": true }] }""");
        var walk = model.Clips.Single(c => c.Name == "Walk");

        var start = HipsAt(model, walk, 0);
        var end = HipsAt(model, walk, walk.FrameCount - 1);
        Assert.True(new Vector2(end.X - start.X, end.Z - start.Z).Length() < 0.01f, $"the hips went from {start} to {end}");
        Assert.True(end.Y - start.Y > 0.1f, $"the hips didn't rise, from {start} to {end}");
    }

    [Fact]
    public void A_clip_walks_away_unless_it_is_in_place()
    {
        var model = Cook(Temp(), """{ "animations": [{ "file": "walk.gltf", "name": "Walk" }] }""");
        var walk = model.Clips.Single(c => c.Name == "Walk");

        var start = HipsAt(model, walk, 0);
        var end = HipsAt(model, walk, walk.FrameCount - 1);
        Assert.True(new Vector2(end.X - start.X, end.Z - start.Z).Length() > 0.5f, $"the hips only went from {start} to {end}");
    }
}
