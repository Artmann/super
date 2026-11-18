using System.Numerics;

namespace Rendition;

public static class Projection
{
    /// <summary>
    /// Reversed-Z perspective with an infinite far plane, for Vulkan's clip space
    /// (y down, depth 0 to 1). Reversed-Z keeps depth precision evenly spread, so
    /// large outdoor scenes do not flicker.
    /// </summary>
    /// <param name="verticalFov">The vertical angle in radians.</param>
    /// <param name="aspect">Width over height.</param>
    /// <param name="near">How close to the eye things are cut off.</param>
    /// <param name="shift">Slides the picture without turning the camera, in half screens, with y up. This makes the frustum asymmetric.</param>
    public static Matrix4x4 PerspectiveInfiniteReversedZ(float verticalFov, float aspect, float near, Vector2 shift = default)
    {
        float f = 1f / MathF.Tan(verticalFov / 2f);
        return new Matrix4x4(
            f / aspect, 0f, 0f, 0f,
            0f, -f, 0f, 0f,
            -shift.X, shift.Y, 0f, -1f,
            0f, 0f, near, 0f);
    }

    /// <summary>
    /// Reversed-Z orthographic for Vulkan's clip space, where things keep their size however
    /// far away they are. It has no eye, so set <see cref="FrameView.CameraPosition"/> far
    /// behind the view: shading works out which way it's looked at from that position.
    /// </summary>
    /// <param name="height">How many world units the screen shows from bottom to top.</param>
    /// <param name="aspect">Width over height.</param>
    /// <param name="near">Where the view starts, from the camera.</param>
    /// <param name="far">Where the view ends, from the camera.</param>
    /// <param name="shift">Slides the picture without moving the camera, in half screens, with y up.</param>
    public static Matrix4x4 OrthographicReversedZ(float height, float aspect, float near, float far, Vector2 shift = default)
    {
        float y = 2f / height;
        float depth = 1f / (far - near);
        return new Matrix4x4(
            y / aspect, 0f, 0f, 0f,
            0f, -y, 0f, 0f,
            0f, 0f, depth, 0f,
            shift.X, -shift.Y, far * depth, 1f);
    }

    /// <summary>
    /// How big a unit looks on a screen this tall. Under perspective that's the pixels one
    /// unit covers one unit away, and under orthographic it's the same at every distance.
    /// </summary>
    internal static (float PixelsAtOneUnit, float PixelsPerUnit) PixelScale(in Matrix4x4 projection, int height)
    {
        float pixels = MathF.Abs(projection.M22) * height / 2f;
        return projection.M34 == 0f ? (0f, pixels) : (pixels, 0f);
    }
}
