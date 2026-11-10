using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

/// <summary>
/// Motion vectors: how far each pixel's surface moved on screen since last frame. The G-buffer pass writes them to
/// <see cref="GBuffer.Motion"/>, from where each vertex is this frame and where it was last frame, and anti-aliasing
/// reads last frame's picture where they point.
/// <para>
/// The encoding: two 16-bit floats, in scene pixels (the render targets' size, not the window's), pointing from where
/// the surface is now back to where it was, with x to the right and y down the picture, as texture coordinates run.
/// Both ends leave out the frame's anti-aliasing shift. So the point a pixel showed last frame is at
/// <c>uv + motion / size</c>, which <see cref="HistoryUv"/> works out. Something moving right one pixel a frame
/// writes (-1, 0). Sky, where no draw wrote, is cleared to (0, 0) and has a depth of 0, so a reader works out the
/// camera's motion there itself; see "Motion vectors" in docs/design.md.
/// </para>
/// This is the CPU mirror of gbuffer.vert and surface.glsl, for tests; it leaves out skinning and swimming, which the
/// shaders do.
/// </summary>
internal static class Motion
{
    /// <summary>The motion from <paramref name="ndcNow"/> back to <paramref name="ndcBefore"/>, in the pixels of a picture <paramref name="scenePixels"/> in size.</summary>
    public static Vector2 Pack(Vector2 ndcNow, Vector2 ndcBefore, Vector2 scenePixels) => (ndcBefore - ndcNow) * 0.5f * scenePixels;

    /// <summary>Where in last frame's picture to read for the pixel at <paramref name="uv"/>, which moved by <paramref name="motion"/>.</summary>
    public static Vector2 HistoryUv(Vector2 uv, Vector2 motion, Vector2 scenePixels) => uv + motion / scenePixels;

    /// <summary>A world point in normalized device coordinates.</summary>
    public static Vector2 Ndc(Vector3 world, Matrix4x4 viewProjection)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        return new Vector2(clip.X, clip.Y) / clip.W;
    }

    /// <summary>
    /// Where a vertex at <paramref name="posed"/>, in its model's space, stands in the world at <paramref name="time"/>:
    /// placed by <paramref name="model"/>, then swayed by <paramref name="wind"/>, packed as the frame block has it, as
    /// windSwayPlaced in wind.glsl does.
    /// </summary>
    public static Vector3 Place(Vector3 posed, Matrix4x4 model, Vector4 wind, float windBase, float sway, float time)
    {
        var world = Vector3.Transform(posed, model);
        var blowing = new Vector3(wind.X, wind.Y, wind.Z);
        if (sway <= 0f || blowing == Vector3.Zero)
        {
            return world;
        }

        float foot = Vector3.Transform(new Vector3(posed.X, windBase, posed.Z), model).Y;
        return world + Wind.Sway(world, world.Y - foot, blowing, blowing.Length(), wind.W, time, sway);
    }

    /// <summary>
    /// The motion a vertex at <paramref name="position"/> writes, placed this frame by <paramref name="model"/> and last
    /// frame as <paramref name="previous"/> says. A draw with no last frame stands where it is now, so it moves with the
    /// camera alone.
    /// </summary>
    public static Vector2 OfVertex(
        Vector3 position, Matrix4x4 model, in PreviousInstance previous, in MotionFrame frame, float windBase, float sway)
    {
        var now = Place(position, model, frame.Wind, windBase, sway, frame.Time);
        var before = previous.HasHistory != 0
            ? Place(position, previous.Model, frame.Wind, windBase, sway, frame.PreviousTime)
            : now;
        return Pack(Ndc(now, frame.ViewProjection), Ndc(before, frame.PreviousViewProjection), frame.Scene);
    }
}

/// <summary>
/// What <see cref="Motion.OfVertex"/> needs of a frame: both frames' cameras without their anti-aliasing shift, the
/// wind as <see cref="Wind.Pack(Vector3, float, float)"/> packs it, both frames' times, and the scene's size in pixels.
/// </summary>
internal readonly record struct MotionFrame(
    Matrix4x4 ViewProjection,
    Matrix4x4 PreviousViewProjection,
    Vector4 Wind,
    float Time,
    float PreviousTime,
    Vector2 Scene);

/// <summary>
/// Where one copy stood last frame, as gbuffer.vert reads it from set 0, binding 9, beside its model matrix at
/// binding 2. Laid out as std430 has it: 96 bytes. With no history, it holds this frame's placing and
/// <see cref="HasHistory"/> is 0, and the vertex shader puts the vertex last frame where it is now.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PreviousInstance
{
    public Matrix4x4 Model;

    /// <summary>The swim's amplitude, body length and phase, as <see cref="SwimMotion"/> has them.</summary>
    public Vector3 Swim;

    /// <summary>Where last frame's skin matrices start in the bone buffer, or -1 for no skin.</summary>
    public int FirstBone;

    /// <summary>1 when the copy has a last frame, 0 when it moves with the camera alone.</summary>
    public int HasHistory;

    private int _pad0;
    private int _pad1;
    private int _pad2;

    /// <summary>A copy with no last frame.</summary>
    public static PreviousInstance None(in DrawItem draw) => new()
    {
        Model = draw.Transform,
        Swim = new Vector3(draw.Swim.Amplitude, draw.Swim.BodyLength, draw.Swim.Phase),
        FirstBone = draw.Bones.Count > 0 ? draw.Bones.First : -1,
        HasHistory = 0,
    };
}

/// <summary>
/// Remembers where each keyed draw stood, so the next frame can say where it was. A key is the game's name for one
/// thing it draws, such as an entity: <see cref="DrawItem.Key"/>. Every part of one model draw shares a key and a
/// placing. Two draws with one key placed differently in a frame can't be told apart, so neither gets a last frame;
/// nor does a draw without a key, or one that wasn't drawn last frame, or any draw when there's no last picture.
/// </summary>
internal sealed class MotionHistory
{
    private Dictionary<int, Placing> _now = [];
    private Dictionary<int, Placing> _last = [];
    private Matrix4x4[] _lastBones = [];
    private Matrix4x4[] _nowBones = [];
    private int _lastBoneCount;
    private int _nowBoneCount;
    private bool _ready;
    private int _boneOffset;

    private struct Placing
    {
        public Matrix4x4 Transform;
        public SwimMotion Swim;
        public BoneRange Bones;
        public bool Shared;
    }

    /// <summary>
    /// Starts a frame of <paramref name="draws"/>, skinned by <paramref name="bones"/>. Last frame's skin matrices are
    /// copied into <paramref name="previousBones"/>, which the bone buffer has right after this frame's, so that last
    /// frame's palette of a draw starts at <c>bones.Length</c> plus where it started last frame. Returns how many
    /// it copied. Without <paramref name="historyReady"/>, as after a resize or on the first frame, nothing has a last frame.
    /// </summary>
    public int Begin(ReadOnlySpan<DrawItem> draws, ReadOnlySpan<Matrix4x4> bones, bool historyReady, Span<Matrix4x4> previousBones)
    {
        (_now, _last) = (_last, _now);
        (_nowBones, _lastBones) = (_lastBones, _nowBones);
        _lastBoneCount = _nowBoneCount;
        _now.Clear();

        foreach (ref readonly var draw in draws)
        {
            if (draw.Key is not { } key)
            {
                continue;
            }

            if (_now.TryGetValue(key, out var seen))
            {
                if (!seen.Shared && !SamePlacing(seen, draw))
                {
                    seen.Shared = true;
                    _now[key] = seen;
                }

                continue;
            }

            _now[key] = new Placing { Transform = draw.Transform, Swim = draw.Swim, Bones = draw.Bones };
        }

        if (_nowBones.Length < bones.Length)
        {
            _nowBones = new Matrix4x4[Math.Max(bones.Length, _nowBones.Length * 2)];
        }

        bones.CopyTo(_nowBones);
        _nowBoneCount = bones.Length;
        _boneOffset = bones.Length;

        // Last frame's palette only fits when there's room for all of it.
        _ready = historyReady && _lastBoneCount <= previousBones.Length;
        if (!_ready)
        {
            return 0;
        }

        _lastBones.AsSpan(0, _lastBoneCount).CopyTo(previousBones);
        return _lastBoneCount;
    }

    /// <summary>Where <paramref name="draw"/> stood last frame, or its placing now with no history.</summary>
    public PreviousInstance Previous(in DrawItem draw)
    {
        if (!_ready || draw.Key is not { } key
            || !_last.TryGetValue(key, out var last) || last.Shared
            || !_now.TryGetValue(key, out var now) || now.Shared
            || last.Bones.Count != draw.Bones.Count)
        {
            return PreviousInstance.None(draw);
        }

        return new PreviousInstance
        {
            Model = last.Transform,
            Swim = new Vector3(last.Swim.Amplitude, last.Swim.BodyLength, last.Swim.Phase),
            FirstBone = last.Bones.Count > 0 ? _boneOffset + last.Bones.First : -1,
            HasHistory = 1,
        };
    }

    private static bool SamePlacing(in Placing placing, in DrawItem draw) =>
        placing.Transform == draw.Transform && placing.Swim == draw.Swim && placing.Bones == draw.Bones;
}
