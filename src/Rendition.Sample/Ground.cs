using System.Numerics;
using Rendition;

/// <summary>A large flat square at height zero, facing up.</summary>
internal static class Ground
{
    private const float HalfSize = 200f;

    private static readonly Vector3 Up = Vector3.UnitY;
    private static readonly Vector4 Tangent = new(1f, 0f, 0f, 1f);

    public static readonly Vertex[] Vertices =
    [
        new(new Vector3(-HalfSize, 0f, -HalfSize), Up, Tangent, new Vector2(0f, 0f)),
        new(new Vector3(-HalfSize, 0f, HalfSize), Up, Tangent, new Vector2(0f, 1f)),
        new(new Vector3(HalfSize, 0f, HalfSize), Up, Tangent, new Vector2(1f, 1f)),
        new(new Vector3(HalfSize, 0f, -HalfSize), Up, Tangent, new Vector2(1f, 0f)),
    ];

    public static readonly uint[] Indices = [0, 1, 2, 0, 2, 3];
}
