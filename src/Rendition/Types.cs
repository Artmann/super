using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex(Vector3 position, Vector3 normal, Vector4 tangent, Vector2 uv)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;

    /// <summary>xyz is the tangent; w is the handedness of the bitangent.</summary>
    public Vector4 Tangent = tangent;

    public Vector2 Uv = uv;
}

public readonly record struct MeshHandle(int Index);

public readonly record struct TextureHandle(int Index);

public readonly record struct MaterialHandle(int Index);

[Flags]
public enum MaterialFlags
{
    None = 0,

    /// <summary>Thin surfaces seen from both sides, such as grilles.</summary>
    TwoSided = 1,

    /// <summary>Needs the transparency pass. Not drawn until that pass exists.</summary>
    Transparent = 2,
}

/// <summary>
/// PBR surface parameters. Constants multiply their textures. The surface texture
/// holds metalness in red, roughness in green and opacity in alpha.
/// </summary>
public struct Material
{
    public Vector4 BaseColor;
    public float Metallic;
    public float Roughness;

    /// <summary>Pixels with opacity below this are cut out. 0 keeps every pixel.</summary>
    public float AlphaCutoff;

    public TextureHandle? BaseColorTexture;
    public TextureHandle? NormalTexture;
    public TextureHandle? SurfaceTexture;
    public MaterialFlags Flags;
}

public struct DrawItem
{
    public MeshHandle Mesh;
    public uint FirstIndex;
    public uint IndexCount;
    public MaterialHandle Material;
    public Matrix4x4 Transform;
}

public struct DirectionalLight
{
    /// <summary>Unit vector pointing from the surface towards the light.</summary>
    public Vector3 Direction;
    public Vector3 Radiance;

    /// <summary>The renderer draws one shadow map, for the first light that sets this.</summary>
    public bool CastsShadow;
}

/// <summary>The part of the world covered by the shadow map.</summary>
public struct ShadowSettings
{
    public Vector3 Center;
    public float Radius;

    /// <summary>
    /// Apparent size of the light: 0 gives hard shadows, larger values blur them
    /// faster with distance from the object casting them.
    /// </summary>
    public float LightAngle;
}

/// <summary>Everything the renderer needs to know about one frame except the draws.</summary>
public ref struct FrameView
{
    public int Width;
    public int Height;
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Vector3 CameraPosition;

    /// <summary>At most eight are used.</summary>
    public ReadOnlySpan<DirectionalLight> Lights;

    public ShadowSettings Shadows;
    public Vector3 AmbientRadiance;

    /// <summary>Shown where nothing was drawn.</summary>
    public Vector3 BackgroundRadiance;

    /// <summary>Surfaces melt into the background between these distances. Equal values turn it off.</summary>
    public float BackgroundFadeStart;
    public float BackgroundFadeEnd;
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
