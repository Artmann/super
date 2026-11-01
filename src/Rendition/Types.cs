using System.Numerics;
using System.Runtime.CompilerServices;
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

/// <summary>A stack of same-sized images that shaders index by layer.</summary>
public readonly record struct TextureArrayHandle(int Index);

public readonly record struct MaterialHandle(int Index);

public readonly record struct SurfaceShaderHandle(int Index);

public readonly record struct ReflectionProbeHandle(int Index);

/// <summary>Bounds and simpler versions for a <see cref="DrawItem"/>. The default stands for none.</summary>
public readonly record struct DetailHandle(int Index);

[Flags]
public enum MaterialFlags
{
    None = 0,

    /// <summary>Thin surfaces seen from both sides, such as grilles.</summary>
    TwoSided = 1,

    /// <summary>Glass and the like: blended over the scene. BaseColor.W sets how much tint it adds.</summary>
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

    /// <summary>Light the surface gives off. Multiplies its texture, so zero means none.</summary>
    public Vector3 Emissive;
    public TextureHandle? EmissiveTexture;

    /// <summary>Strength of a clear glossy layer over the surface, such as car lacquer. 0 to 1.</summary>
    public float Clearcoat;
    public MaterialFlags Flags;

    /// <summary>Replaces the standard surface shader. Transparent materials can't use one yet.</summary>
    public SurfaceShaderHandle? Shader;

    /// <summary>Numbers for a surface shader to use however it likes, as shaderParameters[i].</summary>
    public ShaderParameters ShaderParameters;

    /// <summary>Textures for a surface shader, read with shaderTexture(i, uv). Unset ones read white.</summary>
    public ShaderTextures ShaderTextures;

    /// <summary>Texture arrays for a surface shader, read with shaderLayer(i, uv, layer). Unset ones read white.</summary>
    public ShaderArrays ShaderArrays;
}

/// <summary>Eight vectors a surface shader can read as shaderParameters[0] to [7].</summary>
[InlineArray(Length)]
public struct ShaderParameters
{
    public const int Length = 8;
    private Vector4 _element;
}

/// <summary>Four texture slots a surface shader can read with shaderTexture(0..3, uv).</summary>
[InlineArray(Length)]
public struct ShaderTextures
{
    public const int Length = 4;
    private TextureHandle? _element;
}

/// <summary>Four texture array slots a surface shader can read with shaderLayer(0..3, uv, layer).</summary>
[InlineArray(Length)]
public struct ShaderArrays
{
    public const int Length = 4;
    private TextureArrayHandle? _element;
}

/// <summary>
/// Bends a model in a wave from head to tail. Its head must face +z. Amplitude is how far
/// the tail swings and BodyLength the model's length, both in the model's own units.
/// Raise Phase, in radians, steadily to swim. All zeros leave the model still.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SwimMotion(float Amplitude, float BodyLength, float Phase);

public struct DrawItem
{
    public MeshHandle Mesh;
    public uint FirstIndex;
    public uint IndexCount;
    public MaterialHandle Material;
    public Matrix4x4 Transform;
    public SwimMotion Swim;

    /// <summary>
    /// Lets the renderer skip this draw when it's out of view, and use a simpler version
    /// when it's small on screen. <see cref="Renderer.CreateModel"/> and
    /// <see cref="Renderer.CreateDrawItem"/> fill this in. Without it, the draw always
    /// happens in full.
    /// </summary>
    public DetailHandle Detail;
}

public struct DirectionalLight
{
    /// <summary>Unit vector pointing from the surface towards the light.</summary>
    public Vector3 Direction;
    public Vector3 Radiance;

    /// <summary>The renderer draws one shadow map, for the first light that sets this.</summary>
    public bool CastsShadow;
}

/// <summary>A glowing rectangle in the environment, such as a studio softbox.</summary>
public struct EnvironmentPanel
{
    /// <summary>Points from the centre of the scene towards the panel.</summary>
    public Vector3 Direction;

    /// <summary>Half the panel's size at a distance of one from the scene's centre.</summary>
    public float HalfWidth;
    public float HalfHeight;
    public Vector3 Radiance;
}

/// <summary>The surroundings that surfaces reflect: three bands of colour plus up to eight panels.</summary>
public ref struct EnvironmentDescription
{
    public Vector3 Ceiling;
    public Vector3 Horizon;
    public Vector3 Floor;
    public ReadOnlySpan<EnvironmentPanel> Panels;
}

/// <summary>
/// A spot where the real scene is photographed in all directions, so nearby surfaces
/// reflect it. The box is the room around it: reflections land on its walls and floor
/// instead of infinitely far away, which keeps them lined up with the scene.
/// </summary>
public struct ReflectionProbe
{
    public Vector3 Position;
    public Vector3 BoxMin;
    public Vector3 BoxMax;

    /// <summary>The probe fades out over this distance inside its box.</summary>
    public float BlendDistance;
}

/// <summary>The look of the finished picture. Leaving a value at zero leaves that part unchanged.</summary>
public struct PostSettings
{
    /// <summary>Brightness in photographic stops: +1 is twice as bright.</summary>
    public float Exposure;

    /// <summary>Added contrast. 0.1 is a gentle boost; negative values flatten the picture.</summary>
    public float Contrast;

    /// <summary>Added colour. 0.1 is a gentle boost; -1 is black and white.</summary>
    public float Saturation;

    /// <summary>How much the corners darken, from 0 to 1.</summary>
    public float Vignette;

    /// <summary>Crispness restored after anti-aliasing, from 0 to 1.</summary>
    public float Sharpen;

    /// <summary>Strength of film grain. 0.01 is subtle.</summary>
    public float Grain;
}

/// <summary>Puts the camera underwater. Leaving Absorption at zero means there's no water.</summary>
public struct UnderwaterSettings
{
    /// <summary>Red, green and blue light lost per metre. Red should be the largest, as in the sea.</summary>
    public Vector3 Absorption;

    /// <summary>The water's colour looking up towards the surface.</summary>
    public Vector3 ShallowColor;

    /// <summary>The water's colour looking down.</summary>
    public Vector3 DeepColor;

    /// <summary>Height of the water's surface.</summary>
    public float SurfaceHeight;

    /// <summary>How much the moving net of light brightens sunlit surfaces. 1 doubles them at its peaks.</summary>
    public float CausticStrength;

    /// <summary>How many nets of light fit in a metre. 0.2 makes each about five metres wide.</summary>
    public float CausticScale;

    /// <summary>Brightness of the hazy shafts of light.</summary>
    public float ShaftStrength;

    /// <summary>How far from the camera shafts are drawn, in metres.</summary>
    public float ShaftDistance;
}

/// <summary>The part of the world covered by the shadow maps.</summary>
public struct ShadowSettings
{
    public Vector3 Center;

    /// <summary>How far from the centre the sharpest map reaches, in metres.</summary>
    public float Radius;

    /// <summary>
    /// How many maps to draw, 1 to 4, each covering three times the radius of the one
    /// before. More cover more ground at the same sharpness near the centre, for the
    /// cost of drawing the scene again. 0 counts as 1.
    /// </summary>
    public int Cascades;

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

    /// <summary>Seconds. Reaches shaders as frame.time, for animated surfaces.</summary>
    public float Time;

    /// <summary>At most eight are used.</summary>
    public ReadOnlySpan<DirectionalLight> Lights;

    public ShadowSettings Shadows;

    /// <summary>Scales the light and reflections from the environment.</summary>
    public float EnvironmentIntensity;

    /// <summary>Shown where nothing was drawn.</summary>
    public Vector3 BackgroundRadiance;

    /// <summary>Surfaces melt into the background between these distances. Equal values turn it off.</summary>
    public float BackgroundFadeStart;
    public float BackgroundFadeEnd;

    public PostSettings Post;

    public UnderwaterSettings Water;

    /// <summary>Flat rectangles drawn last, over everything, in order. Needs <see cref="Renderer.SetOverlayAtlas"/>.</summary>
    public ReadOnlySpan<OverlayQuad> Overlay;
}

/// <summary>
/// A flat rectangle on the screen, in pixels from the top left. The atlas gives each
/// pixel's alpha, and color gives the rest, in sRGB as written in an image editor.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct OverlayQuad
{
    public Vector2 Position;
    public Vector2 Size;
    public Vector2 UvMin;
    public Vector2 UvMax;
    public Vector4 Color;
}

/// <summary>How long one render pass took on the GPU.</summary>
public readonly record struct GpuPassTime(string Name, double Milliseconds);

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

    /// <summary>
    /// Draws as fast as the machine can instead of waiting for the display. Frames may
    /// tear. Displays that can't do this keep vsync on.
    /// </summary>
    public bool DisableVsync { get; init; }
}
