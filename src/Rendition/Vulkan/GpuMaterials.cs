using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rendition.Vulkan;

/// <summary>Must match the Material struct in Shaders/material.glsl, laid out as std430.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuMaterial
{
    [InlineArray(Rendition.ShaderTextures.Length)]
    public struct TextureSlots
    {
        private uint _element;
    }

    public GpuSurface Surface;
    public Vector4 Emissive;
    public Rendition.ShaderParameters ShaderParameters;
    public TextureSlots ShaderTextures;
    public TextureSlots ShaderArrays;
    public uint Flags;
    public float Transmission;
    public uint HeightTexture;
    public float HeightDepth;
    public uint DetailColorTexture;
    public uint DetailNormalTexture;

    /// <summary>0 when the material has no detail textures, so the shader skips them.</summary>
    public float DetailScale;

    /// <summary>1 or more; 1 bends nothing.</summary>
    public float Ior;

    /// <summary>How far red, green and blue spread under the surface, in metres, and the widest of them in w.</summary>
    public Vector4 Subsurface;

    public uint MaskTexture;

    /// <summary>How far behind transparent glass the scene must be for it to show fully, in metres; 0 never fades.</summary>
    public float SoftEdge;

    public GpuLayers Layers;

    /// <summary>White without one. <see cref="MaterialFlags.OcclusionOnUv2"/> says which UV set reads it.</summary>
    public uint OcclusionTexture;

    public GpuToon Toon;
}

/// <summary>
/// The first 48 bytes of a <see cref="GpuMaterial"/>: its base colour, metalness, roughness and cutoff, its four main
/// textures and its clearcoat. Every field is four-byte aligned, so grouping them moves no offset.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSurface
{
    public Vector4 BaseColor;
    public float Metallic;
    public float Roughness;
    public float AlphaCutoff;
    public uint BaseColorTexture;
    public uint NormalTexture;
    public uint SurfaceTexture;
    public uint EmissiveTexture;
    public float Clearcoat;
}

/// <summary>The wear and dirt a material's mask lays over it, in a <see cref="GpuMaterial"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLayers
{
    /// <summary>Below 0 keeps the surface's own roughness under the layer.</summary>
    public float WearRoughness;
    public float DirtRoughness;

    /// <summary>The layer's colour in xyz and its amount in w. An amount of 0, as with no mask texture, skips it.</summary>
    public Vector4 Wear;
    public Vector4 Dirt;
}

/// <summary>A toon material's shading, at the end of a <see cref="GpuMaterial"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuToon
{
    /// <summary>How many bands a toon material's light falls into, 0 with a ramp or for a material that isn't toon.</summary>
    public float Steps;

    /// <summary>The toon ramp, white without one.</summary>
    public uint Ramp;

    /// <summary>Unused, so the rim lands on a 16-byte boundary.</summary>
    public float Padding;

    /// <summary>The toon rim's light in rgb; a is unused.</summary>
    public Vector4 Rim;
}

/// <summary>The resource table's own plain textures, which a material without a texture of its own uses.</summary>
internal readonly record struct MaterialDefaults(
    TextureHandle White, TextureHandle FlatNormal, TextureHandle Grey, TextureArrayHandle WhiteArray)
{
    /// <summary>Whether the handle is one of these, which can't be destroyed.</summary>
    public bool Holds(TextureHandle texture) => texture == White || texture == FlatNormal || texture == Grey;

    /// <summary>Whether the handle is the white array, which can't be destroyed.</summary>
    public bool Holds(TextureArrayHandle array) => array == WhiteArray;
}

/// <summary>Turns a <see cref="Material"/> into the <see cref="GpuMaterial"/> the shaders read.</summary>
internal static class GpuMaterials
{
    /// <summary>
    /// The flag bit a toon material has on the GPU, set from <see cref="Material.Toon"/> rather than from
    /// <see cref="MaterialFlags"/>, so a material can't be marked toon without its settings. MATERIAL_TOON in
    /// material_data.glsl.
    /// </summary>
    internal const uint ToonFlag = 1u << 16;

    /// <summary>
    /// The material as the shaders read it, with the table's plain textures where it has none, its shader textures and
    /// its toon shading.
    /// </summary>
    public static GpuMaterial ToGpu(in Material material, in MaterialDefaults defaults)
    {
        var gpu = Surface(material, defaults);
        for (int i = 0; i < ShaderTextures.Length; i++)
        {
            gpu.ShaderTextures[i] = (uint)(material.ShaderTextures[i] ?? defaults.White).Index;
            gpu.ShaderArrays[i] = (uint)(material.ShaderArrays[i] ?? defaults.WhiteArray).Index;
        }

        WriteToon(ref gpu, material.Toon, defaults.White);
        return gpu;
    }

    /// <summary>
    /// How far each colour spreads in metres, with the widest in w, or zero when the
    /// surface doesn't scatter. A colour of zero spreads all three channels the full radius.
    /// </summary>
    internal static Vector4 Spread(in Subsurface subsurface)
    {
        if (!subsurface.IsOn)
        {
            return Vector4.Zero;
        }

        var color = subsurface.Color == Vector3.Zero ? Vector3.One : Vector3.Max(subsurface.Color, Vector3.Zero);
        var spread = color * subsurface.Radius;
        return new Vector4(spread, MathF.Max(spread.X, MathF.Max(spread.Y, spread.Z)));
    }

    /// <summary>
    /// How much light passes through the material, which the shader reads from the transmission slot: a scattering
    /// surface's own, foliage's translucency, or none.
    /// </summary>
    internal static float Transmission(in Material material)
    {
        if (material.Subsurface.IsOn)
        {
            return Math.Clamp(material.Subsurface.Transmission, 0f, 1f);
        }

        return (material.Flags & MaterialFlags.Foliage) != 0 ? Math.Clamp(material.Translucency, 0f, 1f) : 0f;
    }

    /// <summary>The material's share of the wind's lean, never below 0, and 0 for a value that isn't a number.</summary>
    internal static float Sway(in Material material) =>
        float.IsFinite(material.Wind) ? MathF.Max(material.Wind, 0f) : 0f;

    // Without a mask there's nothing to lay the layer by, so it's off.
    private static Vector4 Layer(in MaterialLayer layer, bool masked) =>
        new(layer.Color, masked ? Math.Clamp(layer.Amount, 0f, 1f) : 0f);

    // The material without its shader textures and toon shading, which ToGpu adds.
    private static GpuMaterial Surface(in Material material, in MaterialDefaults defaults) => new()
    {
        Surface = new GpuSurface
        {
            BaseColor = material.BaseColor,
            Metallic = material.Metallic,
            Roughness = material.Roughness,
            AlphaCutoff = material.AlphaCutoff,
            BaseColorTexture = (uint)(material.BaseColorTexture ?? defaults.White).Index,
            NormalTexture = (uint)(material.NormalTexture ?? defaults.FlatNormal).Index,
            SurfaceTexture = (uint)(material.SurfaceTexture ?? defaults.White).Index,
            EmissiveTexture = (uint)(material.EmissiveTexture ?? defaults.White).Index,
            Clearcoat = material.Clearcoat,
        },
        Emissive = new Vector4(material.Emissive, 0f),
        ShaderParameters = material.ShaderParameters,
        Flags = (uint)material.Flags,
        HeightTexture = (uint)(material.HeightTexture ?? defaults.White).Index,
        HeightDepth = material.HeightTexture is null ? 0f : MathF.Max(material.HeightDepth, 0f),
        DetailColorTexture = (uint)(material.DetailColorTexture ?? defaults.Grey).Index,
        DetailNormalTexture = (uint)(material.DetailNormalTexture ?? defaults.FlatNormal).Index,
        DetailScale = material.DetailColorTexture is null && material.DetailNormalTexture is null
            ? 0f
            : material.DetailScale > 0f ? material.DetailScale : 1f,
        Ior = MathF.Max(material.Ior, 1f),
        Transmission = Transmission(material),
        Subsurface = Spread(material.Subsurface),
        MaskTexture = (uint)(material.MaskTexture ?? defaults.White).Index,
        OcclusionTexture = (uint)(material.OcclusionTexture ?? defaults.White).Index,
        Layers = new GpuLayers
        {
            WearRoughness = material.Wear.Roughness,
            DirtRoughness = material.Dirt.Roughness,
            Wear = Layer(material.Wear, material.MaskTexture is not null),
            Dirt = Layer(material.Dirt, material.MaskTexture is not null),
        },
        SoftEdge = float.IsFinite(material.SoftEdge) ? MathF.Max(material.SoftEdge, 0f) : 0f,
    };

    // A toon material's flag, bands or ramp, and rim. Plain bands, and a material that isn't toon, keep the white
    // texture in the ramp's slot.
    private static void WriteToon(ref GpuMaterial gpu, ToonShading? shading, TextureHandle white)
    {
        gpu.Toon.Ramp = (uint)white.Index;
        if (shading is not { } toon)
        {
            return;
        }

        gpu.Flags |= ToonFlag;
        gpu.Toon.Steps = toon.Ramp is null ? toon.Steps : 0f;
        gpu.Toon.Ramp = (uint)(toon.Ramp ?? white).Index;
        gpu.Toon.Rim = new Vector4(toon.Rim, 0f);
    }
}
