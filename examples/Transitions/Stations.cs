using System.Numerics;
using Super;

namespace TransitionsExample;

/// <summary>The plaza's three stations of shapes, warm, blue and green, some of them turning and bouncing.</summary>
internal static class Stations
{
    // The stations' middles, left to right.
    public static readonly Vector3 Warm = new(-7f, 0f, 0f);
    public static readonly Vector3 Blue = new(0f, 0f, -3f);
    public static readonly Vector3 Green = new(7f, 0f, 0f);

    /// <summary>Draws the three stations, their shapes moved by <paramref name="angle"/>.</summary>
    public static void Draw(View view, float angle)
    {
        DrawWarm(view, angle);
        DrawBlue(view, angle);
        DrawGreen(view, angle);
    }

    private static void DrawWarm(View view, float angle)
    {
        var at = Warm;
        view.Model(10, ModelRef.Cube, Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateTranslation(at + new Vector3(-0.8f, 0.6f, 0f)), new Vector3(0.85f, 0.25f, 0.18f));
        view.Model(11, ModelRef.Cube, Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateRotationY(0.4f) * Matrix4x4.CreateTranslation(at + new Vector3(-0.8f, 1.6f, 0f)), new Vector3(0.95f, 0.55f, 0.2f));
        view.Model(12, ModelRef.Sphere, Matrix4x4.CreateTranslation(at + new Vector3(0.9f, 0.5f + MathF.Abs(MathF.Sin(angle * 3f)) * 1.2f, 0.4f)), new Vector3(0.95f, 0.75f, 0.3f));
        view.Model(13, ModelRef.Cone, Matrix4x4.CreateTranslation(at + new Vector3(0.2f, 0.5f, -1.4f)), new Vector3(0.75f, 0.35f, 0.25f));
    }

    private static void DrawBlue(View view, float angle)
    {
        var at = Blue;
        view.Model(20, ModelRef.Cylinder, Matrix4x4.CreateScale(1.4f, 0.6f, 1.4f) * Matrix4x4.CreateTranslation(at + new Vector3(0f, 0.3f, 0f)), new Vector3(0.75f, 0.78f, 0.82f));
        view.Model(21, ModelRef.Torus, Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateScale(1.3f) * Matrix4x4.CreateTranslation(at + new Vector3(0f, 1.45f, 0f)), new Vector3(0.2f, 0.4f, 0.9f));
        for (int i = 0; i < 6; i++)
        {
            float around = i * MathF.Tau / 6f + angle * 0.5f;
            var place = at + new Vector3(MathF.Sin(around) * 2.4f, 0.5f, MathF.Cos(around) * 2.4f);
            view.Model(22 + i, ModelRef.Cone, Matrix4x4.CreateScale(0.6f, 1f, 0.6f) * Matrix4x4.CreateTranslation(place), new Vector3(0.3f, 0.55f, 0.85f));
        }
    }

    private static void DrawGreen(View view, float angle)
    {
        var at = Green;
        ReadOnlySpan<float> heights = [0.8f, 1.6f, 2.4f, 1.2f];
        for (int i = 0; i < heights.Length; i++)
        {
            var place = at + new Vector3(-1.2f + i * 0.8f, heights[i] / 2f, -0.6f + (i % 2) * 0.6f);
            view.Model(30 + i, ModelRef.Cylinder, Matrix4x4.CreateScale(0.55f, heights[i], 0.55f) * Matrix4x4.CreateTranslation(place), new Vector3(0.25f, 0.6f + i * 0.08f, 0.3f));
        }

        view.Model(35, ModelRef.Capsule, Matrix4x4.CreateRotationZ(MathF.Sin(angle * 2f) * 0.3f) * Matrix4x4.CreateTranslation(at + new Vector3(1.6f, 1f, 1.2f)), new Vector3(0.6f, 0.85f, 0.35f));
    }
}
