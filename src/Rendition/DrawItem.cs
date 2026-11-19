using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

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

/// <summary>
/// Fades a draw out with distance from the camera, in metres: whole up to <see cref="Start"/>, gone from
/// <see cref="End"/>, and dithered away in between, which anti-aliasing smooths into a fade. Grass that fades out at
/// 60 metres is <c>new DistanceFade(50, 60)</c>. The distance is measured to the middle of the draw's bounds.
/// </summary>
public readonly record struct DistanceFade(float Start, float End)
{
    /// <summary>How much of the draw is kept at a distance, from 1, all of it, to 0, none.</summary>
    public float Keep(float distance)
    {
        if (distance <= Start)
        {
            return 1f;
        }

        if (distance >= End)
        {
            return 0f;
        }

        return (End - distance) / (End - Start);
    }

    /// <summary>Says why a fade can't be used, or returns null when it can.</summary>
    public static string? Problem(DistanceFade fade)
    {
        var start = fade.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var end = fade.End.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!float.IsFinite(fade.Start) || !float.IsFinite(fade.End) || fade.Start < 0f)
        {
            return $"fade distances {start} to {end} must be 0 or more.";
        }

        return fade.End < fade.Start ? $"fade distance end {end} must not be before its start {start}." : null;
    }
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
    /// when it's small on screen. <see cref="RendererAssets.CreateModel"/> and
    /// <see cref="RendererAssets.CreateDrawItem"/> fill this in. Without it, the draw always
    /// happens in full.
    /// </summary>
    public DetailHandle Detail;

    /// <summary>
    /// Which of the frame's <see cref="FrameView.Bones"/> skin this draw, as made by
    /// <see cref="Skeleton.SkinMatrices"/>. Leave it empty for a draw with no skin.
    /// </summary>
    public BoneRange Bones;

    /// <summary>
    /// The game's name for the thing this draws, such as an entity, the same from frame to frame, so the renderer
    /// knows where it stood last frame and can tell how it moved. Every part of one thing shares its key. Null, or a
    /// key two differently placed draws share, moves it with the camera alone, which smears a moving thing a little
    /// under anti-aliasing. See "Motion vectors" in docs/rendering.md.
    /// </summary>
    public int? Key;

    /// <summary>
    /// Marks this draw as static scenery that a bake may cover, with an id made by <see cref="BakeIds.Of"/> that stays the
    /// same from run to run. Zero, the default, means not static. A static draw can't have <see cref="Bones"/> or
    /// <see cref="Swim"/>. The renderer hashes it into each baked area's inputs. See "Bake ids" in docs/design.md.
    /// </summary>
    public ulong BakeId;

    /// <summary>
    /// Fades this draw out with distance from the camera, winning over its material's
    /// <see cref="Material.Fade"/>. Null uses the material's. Needs <see cref="Detail"/> for the distance to be measured
    /// to the middle of the draw; without it the distance is to the draw's origin.
    /// </summary>
    public DistanceFade? Fade;

    // How much of this copy the frame draws, set by DrawSelection: 0 draws it whole, a share above 0 keeps that share
    // of its pixels, and one below 0 keeps the other end of the noise, so a level fading in fills the pixels the one
    // fading out leaves. See "Fades" in docs/design.md.
    internal float Dither;
}

/// <summary>A run of a frame's bone matrices. A count of zero means no skinning.</summary>
public readonly record struct BoneRange(int First, int Count);
