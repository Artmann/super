using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

// Super has an animation Motion of its own, which would win over the using above.
using Motion = Rendition.Passes.Motion;

/// <summary>
/// Motion vectors: how far a point moved on screen since last frame, in scene pixels, from this frame's and last
/// frame's place of each vertex, and which draws have a last frame to compare with.
/// </summary>
public class MotionTests
{
    private const float Tolerance = 1e-3f;

    // 16 m across 1600 pixels and 9 m down 900, so one pixel is exactly 1 cm.
    private static readonly Vector2 Scene = new(1600f, 900f);
    private static readonly Matrix4x4 Camera = Matrix4x4.CreateOrthographic(16f, 9f, 0.1f, 100f);

    // Wind blowing east at strength 0.3, gusty by half, as the Courtyard has it.
    private static readonly Vector4 Breeze = Wind.Pack(Vector3.UnitX, 0.3f, 0.5f);

    [Fact]
    public void A_still_object_under_a_still_camera_has_no_motion()
    {
        var history = new MotionHistory();
        var model = Matrix4x4.CreateTranslation(1.2f, -0.4f, -5f);
        var draw = new DrawItem { Key = 7, Transform = model };
        history.Begin([draw], [], historyReady: true, []);
        history.Begin([draw], [], historyReady: true, []);

        var previous = history.Previous(draw);
        var frame = new MotionFrame(Camera, Camera, Vector4.Zero, Time: 2f, PreviousTime: 2f - 1f / 60f, Scene);
        var motion = Motion.OfVertex(new Vector3(0.3f, 0.5f, 0.1f), model, previous, frame, windBase: 0f, sway: 0f);

        Assert.Equal(1, previous.HasHistory);
        Assert.Equal(Vector2.Zero, motion);
    }

    [Fact]
    public void Moving_right_one_pixel_is_one_pixel()
    {
        // At 1600 pixels across, NDC's width of 2 makes one pixel 2 / 1600 = 0.00125. A point now one pixel right of
        // where it was points back one pixel left, and its history is read one texel, 1 / 1600 of the width, left.
        var packed = Motion.Pack(ndcNow: new Vector2(0.10125f, -0.3f), ndcBefore: new Vector2(0.1f, -0.3f), Scene);

        Assert.Equal(-1f, packed.X, Tolerance);
        Assert.Equal(0f, packed.Y, Tolerance);
        var uv = new Vector2(0.55f, 0.35f);
        var historyUv = Motion.HistoryUv(uv, packed, Scene);
        Assert.Equal(0.55f - 1f / 1600f, historyUv.X, 1e-6f);
        Assert.Equal(0.35f, historyUv.Y, 1e-6f);

        // The same through a draw: moved 1 cm right under a camera that shows 1 cm a pixel.
        var history = new MotionHistory();
        var before = Matrix4x4.CreateTranslation(0f, 0f, -5f);
        var now = Matrix4x4.CreateTranslation(0.01f, 0f, -5f);
        history.Begin([new DrawItem { Key = 3, Transform = before }], [], historyReady: true, []);
        var moved = new DrawItem { Key = 3, Transform = now };
        history.Begin([moved], [], historyReady: true, []);

        var frame = new MotionFrame(Camera, Camera, Vector4.Zero, Time: 1f, PreviousTime: 1f - 1f / 60f, Scene);
        var motion = Motion.OfVertex(new Vector3(0.5f, 0.2f, 0f), now, history.Previous(moved), frame, 0f, 0f);

        Assert.Equal(-1f, motion.X, Tolerance);
        Assert.Equal(0f, motion.Y, Tolerance);
    }

    [Fact]
    public void A_new_draw_falls_back_to_camera_motion()
    {
        // Key 2 wasn't drawn last frame. Wherever it stood then, it gets the camera's motion alone: the camera
        // moved 1 cm right, so a still point is one pixel further right in last frame's picture.
        var history = new MotionHistory();
        history.Begin([new DrawItem { Key = 1, Transform = Matrix4x4.Identity }], [], historyReady: true, []);
        var model = Matrix4x4.CreateTranslation(2f, 1f, -6f);
        var appeared = new DrawItem { Key = 2, Transform = model };
        history.Begin([new DrawItem { Key = 1, Transform = Matrix4x4.Identity }, appeared], [], historyReady: true, []);

        var previous = history.Previous(appeared);
        var cameraBefore = Matrix4x4.CreateTranslation(0.01f, 0f, 0f) * Camera;
        var frame = new MotionFrame(Camera, cameraBefore, Breeze, Time: 3f, PreviousTime: 3f - 1f / 60f, Scene);
        var motion = Motion.OfVertex(new Vector3(0f, 0.8f, 0f), model, previous, frame, windBase: 0f, sway: 1f);

        Assert.Equal(0, previous.HasHistory);
        Assert.Equal(model, previous.Model);
        Assert.Equal(1f, motion.X, Tolerance);
        Assert.Equal(0f, motion.Y, Tolerance);
    }

    [Fact]
    public void Swaying_grass_has_motion_where_the_base_has_none()
    {
        // A blade 1 m tall standing still under a still camera: its foot stays put while its tip moves as far as
        // Wind.Sway says it moved between the two frames' times.
        const float time = 4.2f;
        const float previousTime = time - 1f / 20f; // a slow frame, so the tip moves most of a pixel
        var history = new MotionHistory();
        var model = Matrix4x4.CreateTranslation(-1f, -2f, -5f);
        var blade = new DrawItem { Key = 11, Transform = model };
        history.Begin([blade], [], historyReady: true, []);
        history.Begin([blade], [], historyReady: true, []);

        var previous = history.Previous(blade);
        var frame = new MotionFrame(Camera, Camera, Breeze, time, previousTime, Scene);
        var foot = Motion.OfVertex(Vector3.Zero, model, previous, frame, windBase: 0f, sway: 1f);
        var tip = Motion.OfVertex(Vector3.UnitY, model, previous, frame, windBase: 0f, sway: 1f);

        Assert.Equal(Vector2.Zero, foot);
        var rest = new Vector3(-1f, -1f, -5f);
        var moved = Wind.Sway(rest, 1f, Vector3.UnitX, 0.3f, 0.5f, time) - Wind.Sway(rest, 1f, Vector3.UnitX, 0.3f, 0.5f, previousTime);
        Assert.True(moved.Length() > 0.005f, $"The tip only moved {moved.Length()} m between the frames, too little to tell.");

        // Each centimetre is a pixel; screen y runs down, as Vulkan's does, while the world's runs up.
        Assert.Equal(-moved.X * 100f, tip.X, 0.01f);
        Assert.Equal(-moved.Y * 100f, tip.Y, 0.01f);
    }

    [Fact]
    public void Two_draws_sharing_a_key_in_different_places_have_no_history()
    {
        // The renderer can't tell which was which last frame, so both take the camera's motion.
        var history = new MotionHistory();
        var left = new DrawItem { Key = 4, Transform = Matrix4x4.CreateTranslation(-1f, 0f, 0f) };
        var right = new DrawItem { Key = 4, Transform = Matrix4x4.CreateTranslation(1f, 0f, 0f) };
        history.Begin([left, right], [], historyReady: true, []);
        history.Begin([left, right], [], historyReady: true, []);

        Assert.Equal(0, history.Previous(left).HasHistory);
        Assert.Equal(0, history.Previous(right).HasHistory);
    }

    [Fact]
    public void Parts_of_one_draw_share_their_key_and_keep_their_history()
    {
        var history = new MotionHistory();
        var before = Matrix4x4.CreateTranslation(0f, 0f, -1f);
        var now = Matrix4x4.CreateTranslation(0f, 0f, -2f);
        history.Begin([new DrawItem { Key = 5, Transform = before, FirstIndex = 0 }, new DrawItem { Key = 5, Transform = before, FirstIndex = 36 }], [],
            historyReady: true, []);
        var second = new DrawItem { Key = 5, Transform = now, FirstIndex = 36 };
        history.Begin([new DrawItem { Key = 5, Transform = now, FirstIndex = 0 }, second], [], historyReady: true, []);

        var previous = history.Previous(second);

        Assert.Equal(1, previous.HasHistory);
        Assert.Equal(before, previous.Model);
    }

    [Fact]
    public void A_skinned_draw_gets_last_frames_palette_after_this_frames()
    {
        // Two joints. This frame's palette sits at 0 and 1 of the bone buffer, so last frame's goes at 2 and 3.
        var history = new MotionHistory();
        Matrix4x4[] palette1 = [Matrix4x4.CreateTranslation(1f, 0f, 0f), Matrix4x4.CreateTranslation(2f, 0f, 0f)];
        Matrix4x4[] palette2 = [Matrix4x4.CreateTranslation(3f, 0f, 0f), Matrix4x4.CreateTranslation(4f, 0f, 0f)];
        var first = new DrawItem { Key = 9, Transform = Matrix4x4.Identity, Bones = new BoneRange(0, 2) };
        var written = new Matrix4x4[4];
        history.Begin([first], palette1, historyReady: true, written.AsSpan(2));
        int count = history.Begin([first], palette2, historyReady: true, written.AsSpan(2));

        var previous = history.Previous(first);

        Assert.Equal(2, count);
        Assert.Equal(1, previous.HasHistory);
        Assert.Equal(2, previous.FirstBone);
        Assert.Equal(palette1[0], written[2]);
        Assert.Equal(palette1[1], written[3]);
    }

    [Fact]
    public void Without_history_ready_nothing_has_history_and_the_camera_stays()
    {
        // After a resize or the first frame there's no last picture, so nothing should claim to know where it was.
        var history = new MotionHistory();
        var draw = new DrawItem { Key = 1, Transform = Matrix4x4.CreateTranslation(1f, 0f, 0f) };
        history.Begin([draw], [], historyReady: true, []);
        history.Begin([draw with { Transform = Matrix4x4.Identity }], [], historyReady: false, []);

        Assert.Equal(0, history.Previous(draw).HasHistory);
    }

    [Fact]
    public void A_key_that_draws_another_model_has_no_history()
    {
        // The key was a barrel last frame and is a crate now, as when a game reuses an entity. The barrel's placing
        // says nothing about where the crate was, so the crate moves with the camera alone.
        var history = new MotionHistory();
        var barrel = new DrawItem { Key = 3, Mesh = new MeshHandle(1), Transform = Matrix4x4.CreateTranslation(-1f, 0f, 0f) };
        var crate = new DrawItem { Key = 3, Mesh = new MeshHandle(2), Transform = Matrix4x4.CreateTranslation(1f, 0f, 0f) };
        history.Begin([barrel], [], historyReady: true, []);
        history.Begin([crate], [], historyReady: true, []);

        var previous = history.Previous(crate);

        Assert.Equal(0, previous.HasHistory);
        Assert.Equal(crate.Transform, previous.Model);
    }

    [Fact]
    public void A_key_that_draws_the_same_model_somewhere_else_keeps_its_history()
    {
        // The other side of the test above: the same mesh, moved, still has a last frame.
        var history = new MotionHistory();
        var before = new DrawItem { Key = 3, Mesh = new MeshHandle(1), Transform = Matrix4x4.CreateTranslation(-1f, 0f, 0f) };
        var now = before with { Transform = Matrix4x4.CreateTranslation(1f, 0f, 0f) };
        history.Begin([before], [], historyReady: true, []);
        history.Begin([now], [], historyReady: true, []);

        var previous = history.Previous(now);

        Assert.Equal(1, previous.HasHistory);
        Assert.Equal(before.Transform, previous.Model);
    }

    [Fact]
    public void A_draw_without_a_key_has_no_history()
    {
        var history = new MotionHistory();
        var draw = new DrawItem { Transform = Matrix4x4.CreateTranslation(1f, 0f, 0f) };
        history.Begin([draw], [], historyReady: true, []);
        history.Begin([draw], [], historyReady: true, []);

        Assert.Equal(0, history.Previous(draw).HasHistory);
    }
}
