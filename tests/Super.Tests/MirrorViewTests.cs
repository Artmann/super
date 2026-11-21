using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>The planar reflection's mirrored camera: where it puts things, and what its oblique near plane cuts away.</summary>
public class MirrorViewTests
{
    private const float Water = 0.5f;

    private static readonly Vector3 Eye = new(1f, 2.5f, 3f);

    // Looking along -z and a little down, as a camera over a puddle does.
    private static readonly Matrix4x4 View = Matrix4x4.CreateLookAt(Eye, Eye + new Vector3(0.2f, -0.4f, -1f), Vector3.UnitY);

    private static readonly Matrix4x4 Lens = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 3f, 16f / 9f, 0.1f);

    private static MirrorView Mirror()
    {
        Assert.True(MirrorView.TryMake(View, Lens, Eye, Water, out var mirror), "The camera is above the water, so there is a mirror.");
        return mirror;
    }

    private static Vector4 Clip(in Matrix4x4 viewProjection, Vector3 point) => Vector4.Transform(new Vector4(point, 1f), viewProjection);

    [Fact]
    public void The_mirrored_eye_stands_as_far_below_the_water_as_the_camera_stands_above()
    {
        Assert.Equal(new Vector3(1f, -1.5f, 3f), Mirror().Eye);
    }

    [Fact]
    public void A_point_shows_in_the_mirror_where_the_camera_sees_its_reflection_turned_left_for_right()
    {
        var mirror = Mirror();
        var camera = View * Lens;
        foreach (var point in new[] { new Vector3(0f, 1.2f, -4f), new Vector3(3f, 0.6f, -10f), new Vector3(-2f, 4f, -25f) })
        {
            var inMirror = Clip(mirror.ViewProjection, point);
            // The reflection the camera sees on the water is the point as far under it as it stands over it: 0.5 is the water.
            var below = Clip(camera, point with { Y = 2f * Water - point.Y });
            Assert.True(inMirror.W > 0f && below.W > 0f, $"{point} is behind one of the cameras.");
            Assert.Equal(-below.X / below.W, inMirror.X / inMirror.W, 4);
            Assert.Equal(below.Y / below.W, inMirror.Y / inMirror.W, 4);
        }
    }

    [Fact]
    public void Depth_is_1_on_the_water_and_falls_with_height_and_distance_above_it()
    {
        var mirror = Mirror();
        float onWater = Depth(mirror, new Vector3(0f, Water, -6f));
        float low = Depth(mirror, new Vector3(0f, Water + 0.5f, -6f));
        float high = Depth(mirror, new Vector3(0f, Water + 3f, -6f));
        // Ten times as far from the mirrored eye along the same line of sight.
        float far = Depth(mirror, mirror.Eye + (new Vector3(0f, Water + 3f, -6f) - mirror.Eye) * 10f);

        Assert.Equal(1f, onWater, 4);
        Assert.InRange(low, 0f, 0.9999f);
        Assert.True(high < low, $"Higher over the same spot should be less deep under reversed-Z: {high} against {low}.");
        Assert.True(far < high, $"Farther along the same line of sight should be less deep: {far} against {high}.");
    }

    [Fact]
    public void What_lies_under_the_water_is_cut_away()
    {
        var mirror = Mirror();
        foreach (var point in new[] { new Vector3(0f, Water - 0.05f, -6f), new Vector3(2f, Water - 2f, -20f) })
        {
            Assert.True(Depth(mirror, point) > 1f, $"{point} is under the water but has depth {Depth(mirror, point)}.");
        }
    }

    [Fact]
    public void The_far_ends_of_the_view_keep_depth_above_0()
    {
        var mirror = Mirror();
        Matrix4x4.Invert(View * Lens, out var inverse);
        int seen = 0;
        for (int corner = 0; corner < 4; corner++)
        {
            var far = Vector4.Transform(new Vector4((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, 0f, 1f), inverse);
            var direction = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z));
            if (Vector3.Dot(direction, new Vector3(0.2f, -0.4f, -1f)) < 0f)
            {
                direction = -direction;
            }

            // The mirrored camera looks along the camera's corners turned over the water.
            var rising = direction with { Y = -direction.Y };
            if (rising.Y <= 0f)
            {
                continue;
            }

            seen++;
            float depth = Depth(mirror, mirror.Eye + rising * 100_000f);
            Assert.InRange(depth, 0f, 1f);
        }

        Assert.True(seen > 0, "No corner of the view rises above the water in the mirror, so nothing was checked.");
    }

    // Straight down at 45 degrees, as a camera over a board game does, seeing 10 m from bottom to top and 60 m deep.
    private static readonly Matrix4x4 FlatView = Matrix4x4.CreateLookAt(Eye, Eye + new Vector3(0f, -1f, -1f), Vector3.UnitY);

    private static readonly Matrix4x4 FlatLens = Projection.OrthographicReversedZ(10f, 16f / 9f, 0.1f, 60f);

    [Fact]
    public void An_orthographic_camera_mirrors_points_where_it_sees_their_reflections()
    {
        Assert.True(MirrorView.TryMake(FlatView, FlatLens, Eye, Water, out var mirror), "The camera is above the water, so there is a mirror.");
        var camera = FlatView * FlatLens;
        foreach (var point in new[] { new Vector3(0f, 1.2f, -4f), new Vector3(3f, 0.6f, -10f), new Vector3(-2f, 4f, -8f) })
        {
            var inMirror = Clip(mirror.ViewProjection, point);
            var below = Clip(camera, point with { Y = 2f * Water - point.Y });
            Assert.Equal(-below.X / below.W, inMirror.X / inMirror.W, 4);
            Assert.Equal(below.Y / below.W, inMirror.Y / inMirror.W, 4);
        }
    }

    [Fact]
    public void An_orthographic_mirror_keeps_the_highest_thing_it_sees_in_depth_and_cuts_what_is_under_the_water()
    {
        Assert.True(MirrorView.TryMake(FlatView, FlatLens, Eye, Water, out var mirror), "The camera is above the water, so there is a mirror.");

        // The far top of the camera's box, turned over the water: the highest thing the mirror can show, about 44 m up.
        Matrix4x4.Invert(FlatView * FlatLens, out var inverse);
        var corner = Vector4.Transform(new Vector4(0f, 1f, 0f, 1f), inverse);
        var highest = new Vector3(corner.X, 2f * Water - corner.Y, corner.Z) / corner.W;
        Assert.True(highest.Y > Water + 10f, $"The view's far top corner mirrors to {highest}, not far over the water.");

        float onWater = Depth(mirror, new Vector3(0f, Water, -6f));
        float low = Depth(mirror, new Vector3(0f, Water + 2f, -6f));
        float top = Depth(mirror, highest);
        float under = Depth(mirror, new Vector3(0f, Water - 0.05f, -6f));

        Assert.Equal(1f, onWater, 4);
        Assert.InRange(top, 0f, 0.01f);
        Assert.True(top < low && low < onWater, $"Depth should fall with height over the water: {onWater}, then {low}, then {top}.");
        Assert.True(under > 1f, $"A point under the water has depth {under}, so it isn't cut away.");
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.505f)]
    [InlineData(3f)]
    public void There_is_no_mirror_when_the_camera_is_not_above_the_water(float eyeHeight)
    {
        var eye = Eye with { Y = eyeHeight };
        Assert.False(MirrorView.TryMake(View, Lens, eye, eyeHeight > 2f ? 3.5f : Water, out _));
    }

    private static float Depth(in MirrorView mirror, Vector3 point)
    {
        var clip = Clip(mirror.ViewProjection, point);
        return clip.Z / clip.W;
    }
}
