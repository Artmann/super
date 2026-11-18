using System.Numerics;
using Rendition;

namespace Super;

/// <summary>A <see cref="Surface"/>'s own values as a Rendition material, which generated meshes and decals both start from.</summary>
internal static class SurfaceMaterials
{
    /// <summary>A surface's own values as a material, before its textures and shader are found. Below full opacity it's glass.</summary>
    internal static Material MaterialOf(Surface surface) => new()
    {
        BaseColor = new Vector4(surface.Color, Math.Clamp(surface.Opacity, 0f, 1f)),
        Roughness = surface.Roughness,
        Metallic = surface.Metallic,
        Flags = FlagsOf(surface),
        Glass = new MaterialGlass { Ior = surface.Ior, SoftEdge = surface.SoftEdge },
        Translucency = (surface.Foliage ?? new Foliage()).Translucency,
        Wind = surface.Wind,
        Fade = surface.FadeDistance,
    };

    /// <summary>A generated surface's material switches: glass below full opacity, foliage drawn from both sides.</summary>
    internal static MaterialFlags FlagsOf(Surface surface) =>
        (surface.Opacity < 1f ? MaterialFlags.Transparent : 0) | (surface.Detail is { Mask: true } ? MaterialFlags.DetailMask : 0)
        | (surface.Features.HasFlag(SurfaceFeatures.WorldVariation) ? MaterialFlags.WorldVariation : 0)
        | (surface.Features.HasFlag(SurfaceFeatures.Dithered) ? MaterialFlags.Dithered : 0)
        | (surface.Features.HasFlag(SurfaceFeatures.Puddles) ? MaterialFlags.Puddles : 0)
        | (surface.Foliage is not null ? MaterialFlags.Foliage | MaterialFlags.TwoSided : 0)
        | (surface.Occlusion is { OnUv2: true } ? MaterialFlags.OcclusionOnUv2 : 0)
        | (surface.Features.HasFlag(SurfaceFeatures.Outline) ? MaterialFlags.Outline : 0)
        | (surface.Features.HasFlag(SurfaceFeatures.NoDecals) ? MaterialFlags.NoDecals : 0);
}
