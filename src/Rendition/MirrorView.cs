using System.Numerics;

namespace Rendition;

/// <summary>
/// The camera seen in a flat, level mirror: the view and projection that draw what a reflective plane at some height
/// shows. The world is turned over the plane, so the mirrored eye stands as far below it as the camera stands above.
/// Clip x is flipped back, so the turned-over world keeps its triangles' winding. The near plane is swapped for the
/// mirror itself, as in Eric Lengyel's "Oblique View Frustum Depth Projection and Clipping" (2005): what lies below it
/// is cut away, and depth is 1 on the plane and falls towards 0 far above it, reversed-Z as the camera's is.
/// See "Planar reflections" in docs/design.md.
/// </summary>
internal readonly record struct MirrorView(Matrix4x4 ViewProjection, Vector3 Eye)
{
    /// <summary>How far above the plane the camera must be, in metres, for the mirror to show anything.</summary>
    public const float LowestCamera = 0.01f;

    /// <summary>
    /// How much of the depth range the steepest corner of the view may use up at its far end. Just under all of it, so
    /// the farthest thing the mirror sees stays at depth above 0, which the depth clear leaves for nothing drawn.
    /// </summary>
    private const float DepthReach = 0.999f;

    /// <summary>
    /// The mirror of a camera with <paramref name="view"/> and <paramref name="projection"/>, the unshifted ones, at
    /// <paramref name="eye"/>, in a plane at <paramref name="height"/>. False when the camera isn't above the plane by
    /// <see cref="LowestCamera"/>, or the matrices can't be inverted, and then nothing is reflected.
    /// </summary>
    public static bool TryMake(in Matrix4x4 view, in Matrix4x4 projection, Vector3 eye, float height, out MirrorView mirror)
    {
        mirror = default;
        if (!(eye.Y - height >= LowestCamera))
        {
            return false;
        }

        var turned = Matrix4x4.CreateReflection(new Plane(0f, 1f, 0f, -height));
        var plain = turned * view * projection * Matrix4x4.CreateScale(-1f, 1f, 1f);
        if (!Matrix4x4.Invert(plain, out var inverse))
        {
            return false;
        }

        // An orthographic camera's w is 1 everywhere, so its view ends at a far plane rather than at infinity.
        var w = new Vector3(plain.M14, plain.M24, plain.M34);
        float steepest = w == Vector3.Zero ? Highest(inverse, height) : Steepest(inverse, w);
        float lean = steepest > 1e-4f ? DepthReach / steepest : 1f;

        // Clip z becomes w less lean times the height above the plane, so depth is 1 - lean * above / w.
        var oblique = plain;
        oblique.M13 = plain.M14;
        oblique.M23 = plain.M24 - lean;
        oblique.M33 = plain.M34;
        oblique.M43 = plain.M44 + lean * height;
        mirror = new MirrorView(oblique, new Vector3(eye.X, 2f * height - eye.Y, eye.Z));
        return true;
    }

    /// <summary>
    /// How high a point stands over the plane against its w, at most, through the view's four corners as they go
    /// on forever: depth 0 on screen is a point at infinity under reversed-Z. Rising least towards the far end of
    /// the view, a corner is where the most is.
    /// </summary>
    private static float Steepest(in Matrix4x4 inverse, Vector3 w)
    {
        float steepest = 0f;
        for (int corner = 0; corner < 4; corner++)
        {
            var clip = new Vector4((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, 0f, 1f);
            var far = Vector4.Transform(clip, inverse);
            var direction = new Vector3(far.X, far.Y, far.Z);
            float ahead = Vector3.Dot(w, direction);
            if (ahead < 0f)
            {
                (direction, ahead) = (-direction, -ahead);
            }

            if (ahead > 1e-6f)
            {
                steepest = MathF.Max(steepest, direction.Y / ahead);
            }
        }

        return steepest;
    }

    /// <summary>
    /// How high over the plane the box an orthographic camera sees reaches, at most, through its eight corners: with w
    /// 1 everywhere, depth falls with height alone, so the highest corner is where the most is.
    /// </summary>
    private static float Highest(in Matrix4x4 inverse, float height)
    {
        float highest = 0f;
        for (int corner = 0; corner < 8; corner++)
        {
            var clip = new Vector4((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? 0f : 1f, 1f);
            var point = Vector4.Transform(clip, inverse);
            highest = MathF.Max(highest, point.Y / point.W - height);
        }

        return highest;
    }
}
