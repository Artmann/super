using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex(Vector3 position, Vector3 normal)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
}

public readonly record struct MeshHandle(int Index);

/// <summary>Constant PBR surface parameters. Textures will extend this later.</summary>
public struct Material
{
    public Vector3 BaseColor;
    public float Metallic;
    public float Roughness;
}

public struct DrawItem
{
    public MeshHandle Mesh;
    public Matrix4x4 Transform;
    public Material Material;
}

public struct DirectionalLight
{
    /// <summary>Unit vector pointing from the surface towards the light.</summary>
    public Vector3 Direction;
    public Vector3 Radiance;
}

/// <summary>Everything the renderer needs to know about one frame except the draws.</summary>
public struct FrameView
{
    public int Width;
    public int Height;
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Vector3 CameraPosition;
    public DirectionalLight Sun;
    public Vector3 AmbientRadiance;
}

public static class Projection
{
    /// <summary>
    /// Reversed-Z perspective with an infinite far plane, for Vulkan's clip space
    /// (y down, depth 0 to 1). Reversed-Z keeps depth precision evenly spread, so
    /// large outdoor scenes do not flicker.
    /// </summary>
    public static Matrix4x4 PerspectiveInfiniteReversedZ(float verticalFov, float aspect, float near)
    {
        float f = 1f / MathF.Tan(verticalFov / 2f);
        return new Matrix4x4(
            f / aspect, 0f, 0f, 0f,
            0f, -f, 0f, 0f,
            0f, 0f, 0f, -1f,
            0f, 0f, near, 0f);
    }
}

/// <summary>Connects the renderer to a window without depending on a windowing library.</summary>
public sealed class RendererOptions
{
    public required IReadOnlyList<string> InstanceExtensions { get; init; }

    /// <summary>Receives the VkInstance handle and returns a VkSurfaceKHR handle.</summary>
    public required Func<nint, ulong> CreateSurface { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public bool EnableValidation { get; init; }
}
