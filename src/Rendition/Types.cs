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

    /// <summary>Four joint indices, one per byte, for a skinned model. Zero for a model with no skin.</summary>
    public uint Joints;

    /// <summary>How much each joint moves this vertex, as four bytes from 0 to 255 that add up to 255. Zero when there is no skin.</summary>
    public uint Weights;

    /// <summary>
    /// A second set of UVs, in which no two triangles share texels, for textures baked from the mesh's own shape such
    /// as <see cref="Material.MaskTexture"/>. Zero when the mesh has none.
    /// </summary>
    public Vector2 Uv2;

    /// <summary>The joints this vertex follows, most important first, and how much it follows each.</summary>
    public void SetSkin(ReadOnlySpan<(int Joint, float Weight)> influences)
    {
        Joints = 0;
        Weights = 0;
        float total = 0f;
        foreach (var (_, weight) in influences)
        {
            total += weight;
        }

        if (total <= 0f)
        {
            return;
        }

        // Rounding has to leave the bytes adding up to 255, or the vertex shrinks or grows.
        int remaining = 255;
        for (int i = 0; i < influences.Length && i < 4; i++)
        {
            var (joint, weight) = influences[i];
            if (joint is < 0 or > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(influences), joint, "A joint index must be 0 to 255.");
            }

            int share = i == influences.Length - 1 || i == 3 ? remaining : Math.Min(remaining, (int)MathF.Round(weight / total * 255f));
            remaining -= share;
            Joints |= (uint)joint << (8 * i);
            Weights |= (uint)share << (8 * i);
        }
    }
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

    /// <summary>
    /// Leaves on a tree's crown. Back faces keep the normal the mesh gives them instead
    /// of flipping it, and sunlight shines through from behind. Pair it with normals
    /// that point out of the crown, as <see cref="FoliageNormals"/> makes, so the crown
    /// is lit as one shape and not as a pile of separate cards.
    /// </summary>
    Foliage = 4,

    /// <summary>
    /// The surface texture's blue channel says how strongly the detail textures show, from
    /// none at black to full at white. Without it they show everywhere.
    /// </summary>
    DetailMask = 8,

    /// <summary>
    /// Darkens and lightens the colour in broad patches laid by where the surface is in the world, not by its uv,
    /// with a little roughness to match, so a texture that repeats across a big wall or floor doesn't show it.
    /// </summary>
    WorldVariation = 16,

    /// <summary>
    /// Opacity fades the surface out softly instead of cutting it: a share of its pixels as large as the opacity
    /// is drawn, a different share each frame, and anti-aliasing blends them with what's behind. For decals such as
    /// stains and grime laid just in front of another surface. Without anti-aliasing the pattern shows as grain.
    /// </summary>
    Dithered = 32,

    /// <summary>
    /// Where the surface lies below the frame's <see cref="FrameView.Puddles"/> level it is water: a rippled mirror
    /// over a colour that darkens with depth. Just above, it is damp: darker and smoother, fading to dry over the
    /// band. Its height map counts, so the hollows flood before the tops. Without a level it does nothing.
    /// </summary>
    Puddles = 64,
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

    /// <summary>
    /// Pixels with opacity below this are cut out. 0 keeps every pixel. Opacity is the
    /// base colour texture's alpha times BaseColor.W times the surface texture's alpha,
    /// so leaf alpha can live in either texture.
    /// </summary>
    public float AlphaCutoff;

    public TextureHandle? BaseColorTexture;
    public TextureHandle? NormalTexture;
    public TextureHandle? SurfaceTexture;

    /// <summary>Light the surface gives off. Multiplies its texture, so zero means none.</summary>
    public Vector3 Emissive;
    public TextureHandle? EmissiveTexture;

    /// <summary>Strength of a clear glossy layer over the surface, such as car lacquer. 0 to 1.</summary>
    public float Clearcoat;

    /// <summary>
    /// A height map in the red channel: white is the top of the surface and black its
    /// deepest point. Parallax occlusion mapping shifts the other textures with it, so
    /// grooves look sunk in. Needs <see cref="HeightDepth"/>.
    /// </summary>
    public TextureHandle? HeightTexture;

    /// <summary>How far below the top black in the height map lies, in metres. 0 turns parallax off.</summary>
    public float HeightDepth;

    /// <summary>
    /// Fine grain over the colour, tiled <see cref="DetailScale"/> times as often. Store it as
    /// linear data: mid grey (128) leaves the colour alone, white doubles it, black darkens it to nothing.
    /// </summary>
    public TextureHandle? DetailColorTexture;

    /// <summary>A second normal map tiled <see cref="DetailScale"/> times as often, blended over the first.</summary>
    public TextureHandle? DetailNormalTexture;

    /// <summary>How many times the detail textures repeat for each repeat of the others. 0 counts as 1.</summary>
    public float DetailScale;
    public MaterialFlags Flags;

    /// <summary>
    /// How much transparent glass bends what's behind it: 1 or less is straight through,
    /// as a thin window, and about 1.5 is solid glass, such as a bottle or a marble.
    /// </summary>
    public float Ior;

    /// <summary>
    /// Light that spreads under the surface before it comes back out, as in skin, wax and
    /// marble. A radius of 0, the default, turns it off. It can't be combined with
    /// clearcoat or foliage, which use the same G-buffer channel.
    /// </summary>
    public Subsurface Subsurface;

    /// <summary>
    /// Masks baked from the mesh's shape, read through its second UV set, <see cref="Vertex.Uv2"/>: red is curvature,
    /// with 0.5 flat and above it convex, green how much of the sky the surface sees, blue height above the mesh's
    /// foot, 0 there and 1 at 30 cm or the mesh's top, and alpha how much an edge there wears, a slow noise so wear
    /// comes and goes. <see cref="Wear"/> and <see cref="Dirt"/> are laid by them. Without one, neither shows. The
    /// mask's edges clamp rather than wrap, since it's an atlas of islands.
    /// </summary>
    public TextureHandle? MaskTexture;

    /// <summary>
    /// Worn edges: where the mask says the surface is convex, the colour is multiplied by twice the layer's colour,
    /// so 0.5 leaves it as it is and the grain shows through, and the roughness turns to the layer's.
    /// </summary>
    public MaterialLayer Wear;

    /// <summary>
    /// Dirt: in hollows the sky can't reach, and low down near the mesh's foot, the colour is multiplied by twice the
    /// layer's colour and the roughness turns to the layer's.
    /// </summary>
    public MaterialLayer Dirt;

    /// <summary>Replaces the standard surface shader. Transparent materials can't use one yet.</summary>
    public SurfaceShaderHandle? Shader;

    /// <summary>Numbers for a surface shader to use however it likes, as shaderParameters[i].</summary>
    public ShaderParameters ShaderParameters;

    /// <summary>Textures for a surface shader, read with shaderTexture(i, uv). Unset ones read white.</summary>
    public ShaderTextures ShaderTextures;

    /// <summary>Texture arrays for a surface shader, read with shaderLayer(i, uv, layer). Unset ones read white.</summary>
    public ShaderArrays ShaderArrays;

    /// <summary>
    /// For <see cref="MaterialFlags.Foliage"/>, how much sunlight shines through from behind, from 0 for none to 1 for
    /// all of it. Leaves are about <see cref="DefaultTranslucency"/>, thin blades more. Other materials ignore it.
    /// </summary>
    public float Translucency;

    /// <summary>How much sunlight foliage lets through when nothing says: about what a leaf does.</summary>
    public const float DefaultTranslucency = 0.6f;

    /// <summary>
    /// How far the surface sways in the frame's <see cref="FrameView.Wind"/>, as a share of its strength: 1 is grass,
    /// less is stiffer, and 0, the default, keeps it still. Vertices sway more the higher they are above the lowest
    /// point of their part, so its foot stays put. Shadows sway with it.
    /// </summary>
    public float Wind;

    /// <summary>Says why a foliage translucency can't be used, or returns null when it can.</summary>
    public static string? TranslucencyProblem(float translucency) =>
        translucency is >= 0f and <= 1f
            ? null
            : $"foliage translucency {translucency.ToString(System.Globalization.CultureInfo.InvariantCulture)} must be from 0 to 1.";

    /// <summary>Says why a wind sway can't be used, or returns null when it can.</summary>
    public static string? WindProblem(float wind) =>
        float.IsFinite(wind) && wind >= 0f
            ? null
            : $"wind sway {wind.ToString(System.Globalization.CultureInfo.InvariantCulture)} must be 0 or more.";
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

/// <summary>
/// A model drawn in one colour that glows a little, so it reads the same over any model,
/// dark or light, lit or in shadow: a building being placed, say. Its shape still shows
/// through the light. Opacity below 1 lets the world show through, as glass does. A ghost
/// casts no shadow. The colour is linear, from 0 to 1.
/// </summary>
public readonly record struct Ghost(Vector3 Color, float Opacity = 1f)
{
    /// <summary>Whether the world shows through, which draws it with glass rather than solid things.</summary>
    public bool SeeThrough => Opacity < 1f;
}

public struct DrawItem
{
    public MeshHandle Mesh;
    public uint FirstIndex;
    public uint IndexCount;
    public MaterialHandle Material;
    public Matrix4x4 Transform;
    public SwimMotion Swim;

    /// <summary>
    /// Multiplies the material's colour for this draw only, so copies of one textured
    /// model can each have their own hue at no cost. Null leaves the colour alone.
    /// </summary>
    public Vector3? Tint;

    /// <summary>
    /// Draws this in one colour, as a preview of where something will go, in place of its
    /// material's colours, textures and tint. Null draws it as usual.
    /// </summary>
    public Ghost? Ghost;

    /// <summary>
    /// Lets the renderer skip this draw when it's out of view, and use a simpler version
    /// when it's small on screen. <see cref="Renderer.CreateModel"/> and
    /// <see cref="Renderer.CreateDrawItem"/> fill this in. Without it, the draw always
    /// happens in full.
    /// </summary>
    public DetailHandle Detail;

    /// <summary>
    /// Which of the frame's <see cref="FrameView.Bones"/> skin this draw, as made by
    /// <see cref="Skeleton.SkinMatrices"/>. Leave it empty for a draw with no skin.
    /// </summary>
    public BoneRange Bones;
}

/// <summary>A run of a frame's bone matrices. A count of zero means no skinning.</summary>
public readonly record struct BoneRange(int First, int Count);

public struct DirectionalLight
{
    /// <summary>Unit vector pointing from the surface towards the light.</summary>
    public Vector3 Direction;
    public Vector3 Radiance;

    /// <summary>The renderer draws one shadow map, for the first light that sets this.</summary>
    public bool CastsShadow;
}

/// <summary>
/// A light at a point, such as a torch, a lamp or a candle, that dims with distance and
/// ends at its range. Given a cone it's a spot, shining one way only. Any number can light
/// a frame, up to <see cref="MaxPerFrame"/>; see "Point and spot lights" in docs/rendering.md.
/// </summary>
public struct LocalLight
{
    /// <summary>The most one frame can have. The renderer throws past this.</summary>
    public const int MaxPerFrame = 1024;

    /// <summary>
    /// Picks out a light from one frame to the next, so its shadow keeps its sharpness as it
    /// moves. Any number, but no two lights in a frame should share one.
    /// </summary>
    public int Key;

    public Vector3 Position;

    /// <summary>Linear colour times brightness, as it falls on a surface one metre away facing it.</summary>
    public Vector3 Radiance;

    /// <summary>How far the light reaches, in metres. It fades smoothly to nothing there.</summary>
    public float Range;

    /// <summary>The radius a light has when <see cref="Radius"/> is left at 0, in metres.</summary>
    public const float DefaultRadius = 0.1f;

    /// <summary>
    /// How big the glowing part is, in metres, such as a torch's flame. Light can't get much
    /// brighter than it is at this distance, so a bigger radius calms a wall right beside
    /// the light while leaving it as bright a metre away and beyond. A bigger light also
    /// casts softer shadows. 0 means <see cref="DefaultRadius"/>.
    /// </summary>
    public float Radius;

    /// <summary>For a spot, the unit vector it shines along. Ignored for a point light.</summary>
    public Vector3 Direction;

    /// <summary>For a spot, the angle from its axis, in radians, inside which it shines fully.</summary>
    public float InnerAngle;

    /// <summary>
    /// For a spot, the angle from its axis, in radians, where it has faded to nothing; under
    /// π/2. Zero makes a point light, which shines every way.
    /// </summary>
    public float OuterAngle;

    /// <summary>Whether things block it. Shadows are drawn into a shared atlas; see docs/rendering.md.</summary>
    public bool CastsShadow;

    /// <summary>
    /// How strongly it lights the volumetric fog, giving it a halo: 1 is as much as the air
    /// really scatters, 0 leaves it out of the fog, and more makes the halo stronger.
    /// </summary>
    public float Volumetric = 1f;

    public LocalLight()
    {
    }

    /// <summary>Whether it's a spot rather than a point light.</summary>
    public readonly bool IsSpot => OuterAngle > 0f;
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
/// A real clear sky, worked out from the sun (Preetham, Shirley and Smits 1999), in place of the
/// environment's ceiling and horizon. Its sun is the light that casts the shadows, or else the
/// first, and it shows that light as a disc <see cref="ShadowSettings.LightAngle"/> across. The
/// environment's floor still shows below the horizon. Changing it, or that sun, while it is on
/// bakes the environment again, which waits for the GPU. See "Sky" in docs/rendering.md.
/// </summary>
public struct AnalyticSky
{
    /// <summary>
    /// How hazy the air is, from 1.7 (very clear) to 10 (milky). 0 keeps the environment's bands. Rendition
    /// doesn't refuse other values, but draws anything above 0 held to 1.7 to 10: below about 1.2 the
    /// model breaks down at the horizon. Super's SkySettings refuses them.
    /// </summary>
    public float Turbidity;
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
    /// <summary>
    /// Brightness in photographic stops: +1 is twice as bright. With <see cref="AutoExposure"/> on it is
    /// added to what the camera metered, so -0.3 keeps the picture a third of a stop darker than metered.
    /// </summary>
    public float Exposure;

    /// <summary>
    /// Sets the brightness from what the camera sees, the way an eye or a camera does: it meters the lit
    /// frame each frame, before the glow, and eases towards the brightness that puts it at middle grey.
    /// The darkest 40 per cent and the brightest 5 per cent of the pixels are left out of the metering, so
    /// the sun's disc and sparkle on water don't darken the picture. Off, <see cref="Exposure"/> alone
    /// sets the brightness. See docs/rendering.md.
    /// </summary>
    public bool AutoExposure;

    /// <summary>
    /// How fast <see cref="AutoExposure"/> follows a change, in stops a second, which must be above 0. 0
    /// means the default of 2, so stepping from a sunny yard into a room four stops darker settles in about two seconds.
    /// </summary>
    public float AdaptationSpeed;

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

    /// <summary>
    /// Added glow, on top of the soft glow every picture has. 0.1 is a dreamy haze round bright
    /// things; negative values take the glow away.
    /// </summary>
    public float Bloom;

    /// <summary>
    /// Colour added to the darkest parts of the picture, fading out towards white, so shadows
    /// take on a tint instead of going black. 0.02 is gentle; (0.03, 0.02, 0.05) gives lilac shade.
    /// </summary>
    public Vector3 Lift;

    /// <summary>
    /// Extra brightness in each of red, green and blue, which shows most in the brightest parts.
    /// 0.05 is five per cent brighter; (0.05, 0.02, -0.04) warms the highlights to cream.
    /// </summary>
    public Vector3 Gain;

    /// <summary>
    /// The curve that fits the scene's light into what a screen can show. <see cref="Tonemapper.Agx"/>,
    /// the default, keeps bright colours from going flat; <see cref="Tonemapper.Aces"/> is the older,
    /// punchier film look.
    /// </summary>
    public Tonemapper Tonemapper;

    /// <summary>
    /// The look on top of AgX. <see cref="AgxLook.Punchy"/>, the default, deepens the mid-tones and
    /// strengthens colour; <see cref="AgxLook.Base"/> is the plain curve. It has no effect under ACES.
    /// </summary>
    public AgxLook AgxLook;

    /// <summary>
    /// White balance from -1 to 1, with 0 unchanged. Above 0 warms the picture, as if the camera were
    /// balanced for light bluer than daylight (1 is 12000 K); below 0 cools it (-1 is 4000 K).
    /// 0.1 is a gentle afternoon warmth.
    /// </summary>
    public float Temperature;

    /// <summary>
    /// White balance across the other way, from -1 to 1, with 0 unchanged. Above 0 pulls the picture
    /// towards magenta and below 0 towards green, to take out a cast that <see cref="Temperature"/> can't.
    /// </summary>
    public float Tint;

    /// <summary>
    /// Where the camera is focused, in metres in front of it, for depth of field. Only things at this
    /// distance are sharp once <see cref="Aperture"/> opens the lens. It must be further than
    /// <see cref="FocalLength"/>. See docs/rendering.md.
    /// </summary>
    public float FocusDistance;

    /// <summary>
    /// How wide the lens is open, as an f-number: 2.8 blurs strongly, 8 gently, 16 hardly at all.
    /// 0, the default, is no depth of field: everything is sharp, as through a pinhole.
    /// </summary>
    public float Aperture;

    /// <summary>
    /// The lens's focal length in millimetres for depth of field, on a 36 mm wide sensor. Longer lenses
    /// blur more at the same f-number. 0 means the default of 35. It changes only the blur, not the
    /// field of view, which the camera sets.
    /// </summary>
    public float FocalLength;
}

/// <summary>The curve that turns the scene's light into the colours a screen can show.</summary>
public enum Tonemapper
{
    /// <summary>
    /// AgX, as Blender 4 uses by default: bright, strongly coloured light fades towards white
    /// gradually, so a sunlit brick wall keeps its detail instead of going one flat orange.
    /// </summary>
    Agx,

    /// <summary>A fit of the ACES film curve: punchier mid-tones, but saturated highlights go flat.</summary>
    Aces,
}

/// <summary>A look on top of the <see cref="Tonemapper.Agx"/> curve, as Blender offers. ACES ignores it.</summary>
public enum AgxLook
{
    /// <summary>Blender's Punchy look: deeper mid-tones and richer colour, so a sunlit scene doesn't read pale.</summary>
    Punchy,

    /// <summary>The plain AgX curve: softer and paler, with the most room in the highlights.</summary>
    Base,
}

/// <summary>How a <see cref="ScreenTransition"/> brings in the new picture.</summary>
public enum TransitionStyle
{
    /// <summary>The frame as it is.</summary>
    None,

    /// <summary>Mixes the frame towards <see cref="ScreenTransition.Color"/>.</summary>
    Fade,

    /// <summary>Mixes the held picture into the frame evenly.</summary>
    Dissolve,

    /// <summary>The frame sweeps across the held picture behind a straight edge.</summary>
    Wipe,

    /// <summary>The frame opens out of a circle over the held picture.</summary>
    Iris,
}

/// <summary>
/// Moves from one picture to another. A fade mixes the frame towards a colour. A dissolve,
/// wipe or iris shows a held picture, one frame kept from earlier, over the frame, and
/// takes it away as <see cref="Amount"/> goes from 0 to 1. Leaving Style at None shows the
/// frame as it is. It changes the scene's picture only: the overlay is drawn on top.
/// </summary>
public struct ScreenTransition
{
    public TransitionStyle Style;

    /// <summary>
    /// How far along, from 0 to 1. For a fade, how much of the colour shows. For the
    /// others, 0 shows only the held picture and 1 only the frame.
    /// </summary>
    public float Amount;

    /// <summary>What a fade goes to, linear red, green and blue.</summary>
    public Vector3 Color;

    /// <summary>Which way a wipe's edge travels across the screen, with x to the right and y down. Zero goes to the right.</summary>
    public Vector2 Direction;

    /// <summary>Where an iris opens from, as a fraction of the screen: (0.5, 0.5) is the middle.</summary>
    public Vector2 Center;

    /// <summary>How soft a wipe's or an iris's edge is, as a fraction of the way it travels.</summary>
    public float Softness;

    /// <summary>
    /// Changing this number holds a new picture: the frame drawn just before this one, as it
    /// looked before the overlay. Keep it the same to keep the picture held. Before any frame
    /// has been drawn, the held picture is black.
    /// </summary>
    public int Hold;
}

/// <summary>
/// Standing water on surfaces whose material has <see cref="MaterialFlags.Puddles"/>. <see cref="Level"/> is the
/// height of the water in metres; whatever lies below it is under water. <see cref="Band"/> is how far above the
/// water, in metres, the surface is damp, darkest at the waterline and dry at the band's top.
/// </summary>
public readonly record struct PuddleSettings(float Level, float Band = PuddleSettings.DefaultBand)
{
    /// <summary>The damp band's height when none is given, in metres.</summary>
    public const float DefaultBand = 0.08f;
}

/// <summary>
/// Wind that sways materials with <see cref="Material.Wind"/>. <see cref="Direction"/> is the way it blows, flattened
/// onto the ground; <see cref="Strength"/> is how far, in metres, it leans the tip of grass 1 m tall at a gust's peak;
/// <see cref="Gustiness"/> runs from 0 for a steady wind to 1 for one that drops to a fifth of that between gusts.
/// </summary>
public readonly record struct WindSettings(Vector3 Direction, float Strength, float Gustiness = WindSettings.DefaultGustiness)
{
    /// <summary>How gusty the wind is when not said: between gusts it leans things 60 % as far as at their peak.</summary>
    public const float DefaultGustiness = 0.5f;
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

    /// <summary>
    /// How far, in metres, each pixel looks along the light through the depth buffer for
    /// something touching it, which darkens the last few centimetres the shadow maps are
    /// too coarse to see. Only inside the first cascade's radius. 0 turns it off.
    /// </summary>
    public float ContactLength = DefaultContactLength;

    public const float DefaultContactLength = 0.25f;

    public ShadowSettings()
    {
    }
}

/// <summary>
/// How nearby surfaces hide the soft light from the sky. <see cref="Radius"/> is how far, in
/// metres, a point looks for them; <see cref="Strength"/>, from 0 to 1, is how dark they make it.
/// </summary>
public readonly record struct OcclusionSettings(float Radius = OcclusionSettings.DefaultRadius, float Strength = OcclusionSettings.DefaultStrength)
{
    public const float DefaultRadius = 0.6f;
    public const float DefaultStrength = 0.9f;

    /// <summary>The defaults, which suit rooms and streets seen from head height.</summary>
    public OcclusionSettings() : this(DefaultRadius, DefaultStrength)
    {
    }
}

/// <summary>
/// Air that thickens towards the ground and dims and tints what's far away. <see cref="Density"/>
/// is how much light it takes per metre at <see cref="Height"/>, 0 for none; above that height
/// it thins by e every 1 / <see cref="Falloff"/> metres, and below it thickens the same way, so
/// 0 falloff is fog of one density everywhere. It takes the colour of the sky behind, and glows
/// towards the sun by <see cref="SunScatter"/>.
/// </summary>
public readonly record struct FogSettings(float Density = 0f, float Height = 0f, float Falloff = FogSettings.DefaultFalloff, float SunScatter = FogSettings.DefaultSunScatter)
{
    public const float DefaultFalloff = 0.1f;
    public const float DefaultSunScatter = 0.5f;

    /// <summary>No fog yet, with a falloff and sun scatter that suit a day outdoors once given a density.</summary>
    public FogSettings() : this(0f, 0f, DefaultFalloff, DefaultSunScatter)
    {
    }

    public const float DefaultAnisotropy = 0.6f;
    public const float DefaultVolumetricDistance = 48f;

    /// <summary>The most <see cref="Anisotropy"/> can be either way.</summary>
    public const float MaxAnisotropy = 0.9f;

    /// <summary>
    /// Which way the fog scatters light, from -0.9 to 0.9: 0 sends it every way alike, more
    /// sends it onwards, so the air glows most looking towards a light, as real haze does,
    /// and less sends it back. Only the volumetric fog uses it.
    /// </summary>
    public float Anisotropy { get; init; } = DefaultAnisotropy;

    /// <summary>
    /// How far from the camera, in metres, the fog is worked out in a volume that the sun's
    /// shadows and the point and spot lights light up, giving shafts of light and halos.
    /// Past it the plain fog carries on. 0 is the plain fog only.
    /// </summary>
    public float VolumetricDistance { get; init; } = DefaultVolumetricDistance;
}

/// <summary>Everything the renderer needs to know about one frame except the draws.</summary>
public ref struct FrameView
{
    public FrameView()
    {
    }

    public int Width;
    public int Height;
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Vector3 CameraPosition;

    /// <summary>Seconds. Reaches shaders as frame.time, for animated surfaces.</summary>
    public float Time;

    /// <summary>At most eight are used.</summary>
    public ReadOnlySpan<DirectionalLight> Lights;

    /// <summary>Point and spot lights. At most <see cref="LocalLight.MaxPerFrame"/>; more throws.</summary>
    public ReadOnlySpan<LocalLight> LocalLights;

    /// <summary>
    /// Every skinned draw's matrices for this frame, one after another, which draws
    /// point into with <see cref="DrawItem.Bones"/>. At most <see cref="Renderer.MaxBones"/>.
    /// </summary>
    public ReadOnlySpan<Matrix4x4> Bones;

    public ShadowSettings Shadows = new();

    /// <summary>How nearby surfaces darken the soft light. See docs/rendering.md.</summary>
    public OcclusionSettings Occlusion = new();

    /// <summary>
    /// A box of light probes that gives the surfaces inside it bounced light in place of the sky's,
    /// or none. At most <see cref="ProbeGrid.MaxProbes"/>. See docs/rendering.md.
    /// </summary>
    public ProbeVolumeSettings? LightProbes;

    /// <summary>Scales the light and reflections from the environment.</summary>
    public float EnvironmentIntensity;

    /// <summary>A real clear sky in place of the environment's bands, or, with turbidity 0, the bands.</summary>
    public AnalyticSky Sky;

    /// <summary>Shown where nothing was drawn, unless <see cref="ShowEnvironment"/> is set.</summary>
    public Vector3 BackgroundRadiance;

    /// <summary>
    /// Shows the environment's bands where nothing was drawn, scaled by
    /// <see cref="EnvironmentIntensity"/>, so the sky behind the scene is the one lighting it.
    /// Distant surfaces then melt into the sky in their own direction.
    /// </summary>
    public bool ShowEnvironment;

    /// <summary>Surfaces melt into the background between these distances. Equal values turn it off.</summary>
    public float BackgroundFadeStart;
    public float BackgroundFadeEnd;

    /// <summary>
    /// Where the fade's distance is measured from, the camera when unset. Set, the distance is taken
    /// across the ground only, so tall things fade no sooner than what they stand on. A point the camera
    /// looks at suits a camera far off, as an isometric one is, where everything on screen is about as far away.
    /// </summary>
    public Vector3? BackgroundFadeOrigin;

    /// <summary>What distant surfaces fade into. Zero fades them into the background behind them.</summary>
    public Vector3 BackgroundFadeColor;

    /// <summary>
    /// Height fog between the camera and what it sees, lit by the sky and the suns. It's
    /// drawn for the view only: light and reflection probes capture clear air. See docs/rendering.md.
    /// </summary>
    public FogSettings Fog;

    public PostSettings Post;

    public UnderwaterSettings Water;

    /// <summary>
    /// The height water stands at on surfaces whose material has <see cref="MaterialFlags.Puddles"/>, and the damp
    /// band above it. Null leaves every surface dry.
    /// </summary>
    public PuddleSettings? Puddles;

    /// <summary>
    /// The wind that sways materials with <see cref="Material.Wind"/>, and their shadows. Null keeps everything still.
    /// </summary>
    public WindSettings? Wind;

    /// <summary>Flat rectangles drawn last, over everything, in order. Needs <see cref="Renderer.SetOverlayAtlas"/>.</summary>
    public ReadOnlySpan<OverlayQuad> Overlay;

    /// <summary>
    /// Soft puffs of dust, smoke or spray, blended over the lit scene in any order. At most
    /// <see cref="Particle.MaxPerFrame"/> are drawn; the rest are dropped.
    /// </summary>
    public ReadOnlySpan<Particle> Particles;

    /// <summary>
    /// Materials laid onto what lies inside a box, after the opaque surfaces are drawn and before they're lit.
    /// At most <see cref="Passes.DecalBatch.MaxDecals"/>; more throws. Drawn in order, so a later one covers an earlier one.
    /// </summary>
    public ReadOnlySpan<DecalDraw> Decals;

    /// <summary>A fade, dissolve or wipe between two pictures, or none.</summary>
    public ScreenTransition Transition;

    /// <summary>Effects to leave out of this frame. Left empty, every effect is drawn.</summary>
    public RenderEffects Off;
}

/// <summary>
/// The parts of the picture that cost GPU time and can be left out. Each one left out skips
/// its work, not only its look, so a frame without any of them is as cheap as the renderer gets.
/// </summary>
[Flags]
public enum RenderEffects
{
    None = 0,

    /// <summary>The sun's shadow maps. Without them sunlight is blocked only by nearby surfaces, as ambient occlusion sees them.</summary>
    SunShadows = 1 << 0,

    /// <summary>Point and spot light shadows. Without them those lights, too, are blocked only by nearby surfaces.</summary>
    LightShadows = 1 << 1,

    /// <summary>Darkening in corners and creases, worked out from the screen.</summary>
    AmbientOcclusion = 1 << 2,

    /// <summary>Shiny surfaces mirroring what's on screen. Without them they reflect only the sky and reflection probes.</summary>
    Reflections = 1 << 3,

    /// <summary>Smoothing jagged edges over several frames. Without it edges show their pixels.</summary>
    AntiAliasing = 1 << 4,

    /// <summary>The glow round bright things.</summary>
    Bloom = 1 << 5,

    /// <summary>
    /// Every material's colour, textures, normal maps and shine. Without them every surface is the same
    /// light grey, matte clay, shaped only by its mesh, which shows the lighting and the models on their own.
    /// Cut-outs still cut, glows still glow and see-through things stay see-through.
    /// </summary>
    Materials = 1 << 6,

    /// <summary>
    /// The sun's contact shadows, the last centimetres where things meet the ground, found on screen.
    /// Without them the sun's shadow maps alone shade those, as <see cref="ShadowSettings.ContactLength"/> 0 does.
    /// </summary>
    ContactShadows = 1 << 7,

    /// <summary>
    /// The light bounced off walls and floors, from <see cref="FrameView.LightProbes"/>. Without it
    /// nothing is drawn into the probes and every surface takes its soft light from the sky.
    /// </summary>
    LightProbes = 1 << 8,

    /// <summary>
    /// The light that bounces between a rough surface's microfacets before it leaves, given back to its
    /// shine. Without it rough metals come out darker than they should.
    /// It costs a few instructions a pixel.
    /// </summary>
    MultiscatterSpecular = 1 << 9,

    /// <summary>
    /// Worn edges and dirt laid by materials' baked masks (<see cref="Material.Wear"/> and <see cref="Material.Dirt"/>).
    /// Without them those surfaces show only their own colour and roughness, and their masks aren't read.
    /// </summary>
    WearAndDirt = 1 << 10,

    /// <summary>The height fog from <see cref="FrameView.Fog"/>. Without it the air is clear, whatever its density.</summary>
    Fog = 1 << 11,

    /// <summary>
    /// Metering the frame for <see cref="PostSettings.AutoExposure"/>. Without it the fixed
    /// <see cref="PostSettings.Exposure"/> sets the brightness, whatever the frame asks for.
    /// </summary>
    AutoExposure = 1 << 12,

    /// <summary>
    /// Blurring what's nearer or further than <see cref="PostSettings.FocusDistance"/>, as a camera lens
    /// does once <see cref="PostSettings.Aperture"/> opens it. Without it everything is sharp.
    /// </summary>
    DepthOfField = 1 << 13,

    /// <summary>The frame's <see cref="FrameView.Decals"/>. Without them stains and grime aren't drawn.</summary>
    Decals = 1 << 14,

    /// <summary>
    /// Working out the fog near the camera in a volume lit by the sun's shadows and the point and
    /// spot lights, up to <see cref="FogSettings.VolumetricDistance"/>. Without it the fog is the
    /// plain height fog everywhere, with no shafts of light or halos.
    /// </summary>
    VolumetricFog = 1 << 15,

    /// <summary>
    /// Water and a damp band on surfaces whose material has <see cref="MaterialFlags.Puddles"/>, below and just
    /// above <see cref="FrameView.Puddles"/>. Without it every surface is dry.
    /// </summary>
    Puddles = 1 << 16,

    /// <summary>
    /// Materials with <see cref="Material.Wind"/> sway in <see cref="FrameView.Wind"/>, and their shadows with them.
    /// Without it everything stands still.
    /// </summary>
    Wind = 1 << 17,

    All = SunShadows | LightShadows | AmbientOcclusion | Reflections | AntiAliasing | Bloom | Materials | ContactShadows | LightProbes
        | MultiscatterSpecular | WearAndDirt | Fog | AutoExposure | DepthOfField | Decals | VolumetricFog | Puddles | Wind,
}

/// <summary>
/// How much of a decal's material reaches the surface under it, each from 0 to 1, along with
/// how far round a corner it reaches. Each weight is multiplied by the material's opacity.
/// </summary>
/// <param name="Color">How much of the decal's colour covers the surface's.</param>
/// <param name="Normal">How much of the decal's normal map is laid over the surface's bumps.</param>
/// <param name="Roughness">How much of the decal's roughness replaces the surface's, as a damp patch shines.</param>
/// <param name="AngleFade">
/// The cosine of the angle from the box's up at which a surface stops taking the decal. The decal fades out
/// as a surface turns towards it, so 0.5 reaches surfaces up to 60 degrees away, and 0 reaches round to 90.
/// </param>
public readonly record struct DecalBlend(float Color = 1f, float Normal = 1f, float Roughness = 1f, float AngleFade = 0.5f)
{
    /// <summary>The whole material, fading out on surfaces more than 60 degrees from the box's up.</summary>
    public DecalBlend() : this(1f, 1f, 1f, 0.5f)
    {
    }
}

/// <summary>
/// A material laid onto whatever lies inside a box, such as a damp patch on a wall or rust under a
/// bracket. It projects down the box's own −Y: the box's X and Z span the material's width and height, and
/// its Y how deep it reaches. See docs/views.md.
/// </summary>
/// <param name="Box">Takes the cube from −0.5 to 0.5 on each axis to where the decal is in the world.</param>
/// <param name="Material">What is laid on. Its colour, surface and normal textures are used.</param>
/// <param name="Blend">How much of each part of the material reaches the surface.</param>
public readonly record struct DecalDraw(Matrix4x4 Box, MaterialHandle Material, DecalBlend Blend);

/// <summary>
/// A soft, see-through puff that always faces the camera, such as one wisp of dust or
/// smoke. It's lit by the sun, the sky and point and spot lights, falls into the sun's
/// shadow, sits in the fog, fades into haze, and melts into whatever it touches instead of showing a hard
/// edge where it meets the ground. A glowing one, such as a lick of flame, adds its own light.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Particle
{
    /// <summary>How many fit in one frame. Past this they aren't drawn.</summary>
    public const int MaxPerFrame = 32768;

    /// <summary>The middle of the puff.</summary>
    public Vector3 Position;

    /// <summary>How wide the puff is, in metres.</summary>
    public float Size;

    /// <summary>Linear red, green and blue from 0 to 1, and how solid its middle is from 0 to 1.</summary>
    public Vector4 Color;

    /// <summary>Turns the puff on the screen, in radians.</summary>
    public float Rotation;

    /// <summary>Picks the lumpy outline of the puff. Any number; nearby values look alike.</summary>
    public float Variant;

    /// <summary>
    /// Light the puff gives off itself, as a multiple of its colour, on top of the light it's
    /// lit by: 0 for dust, about 1 for embers, and past 1 for flames, which then bloom.
    /// </summary>
    public float Glow;
}

/// <summary>
/// A rectangle on the screen, in pixels from the top left, which can be rounded, bordered
/// and clipped. The atlas gives each pixel's alpha and color gives the rest, in sRGB as
/// written in an image editor. With <see cref="TextureIndex"/> set, a texture gives the
/// pixels and color tints them. A default value for any of these changes nothing.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct OverlayQuad
{
    /// <summary>How many fit in one frame. Past this they aren't drawn.</summary>
    public const int MaxPerFrame = 8192;

    public Vector2 Position;
    public Vector2 Size;
    public Vector2 UvMin;
    public Vector2 UvMax;
    public Vector4 Color;

    /// <summary>The line drawn inside the edge, in sRGB. Needs <see cref="BorderWidth"/>.</summary>
    public Vector4 BorderColor;

    /// <summary>Only what's inside this is drawn: x, y, width, height in pixels. No width means no clip.</summary>
    public Vector4 Clip;

    public float CornerRadius;
    public float BorderWidth;

    /// <summary>Rounds the corners of <see cref="Clip"/>.</summary>
    public float ClipRadius;

    /// <summary>Zero reads the atlas. Anything else comes from <see cref="For"/>.</summary>
    public int TextureIndex;

    /// <summary>The value of <see cref="TextureIndex"/> that draws this texture.</summary>
    public static int For(TextureHandle texture) => texture.Index + 1;
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

    /// <summary>How many texels across each of the sun's cascade maps starts: 1024, 2048 or 4096. See <see cref="Renderer.ShadowResolution"/>.</summary>
    public int ShadowResolution { get; init; } = 2048;

    /// <summary>How large the scene starts out drawn against the window, from 0.5 to 2. See <see cref="Renderer.RenderScale"/>.</summary>
    public float RenderScale { get; init; } = 1f;
}

/// <summary>
/// A layer laid over a material where its mask says, such as wear on its edges or dirt in its hollows. See
/// <see cref="Material.Wear"/>.
/// </summary>
/// <param name="Color">The layer's colour, linear.</param>
/// <param name="Roughness">The layer's roughness, from 0 to 1, or below 0 to keep the surface's own.</param>
/// <param name="Amount">How strongly it shows, from 0, which turns it off, to 1.</param>
public readonly record struct MaterialLayer(Vector3 Color, float Roughness, float Amount);

/// <summary>
/// How light spreads under a surface. The lighting pass blurs the diffuse light of these
/// surfaces across the screen, further for the colours that travel further, so shadow
/// edges soften and take on a warm rim, as on skin.
/// </summary>
public struct Subsurface
{
    /// <summary>The longest distance light spreads under the surface, in metres. Skin is about 0.01. 0 turns it off.</summary>
    public float Radius;

    /// <summary>
    /// How far red, green and blue each spread, as fractions of <see cref="Radius"/>.
    /// Skin lets red go furthest, so about (1, 0.4, 0.25). Zero counts as one for all three.
    /// </summary>
    public Vector3 Color;

    /// <summary>
    /// How much light from behind shines through thin parts, such as ears or a candle's
    /// rim, from 0 to 1. It's tinted by the base colour and <see cref="Color"/>.
    /// </summary>
    public float Transmission;

    /// <summary>True when the surface scatters light at all.</summary>
    public readonly bool IsOn => Radius > 0f;

    /// <summary>
    /// What stops a material from having this subsurface, or null when nothing does.
    /// Clearcoat, foliage and subsurface share a G-buffer channel.
    /// </summary>
    public readonly string? Problem(float clearcoat, MaterialFlags flags)
    {
        if (float.IsNaN(Radius) || Radius < 0f)
        {
            return "subsurface radius must be 0 or more.";
        }

        if (!IsOn)
        {
            return null;
        }

        if (clearcoat > 0f)
        {
            return "subsurface can't be combined with clearcoat, because they share a G-buffer channel.";
        }

        if ((flags & MaterialFlags.Foliage) != 0)
        {
            return "subsurface can't be combined with foliage, because they share a G-buffer channel.";
        }

        return null;
    }
}
